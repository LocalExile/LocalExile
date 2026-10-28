using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;
using GameServer.Storage;

/// <summary>Validates imported progress before writing a new character and its complete save in one transaction.</summary>
static class PathOfBuildingCharacters
{
    public sealed record Progress(CharacterClass Class, int Level, uint Experience, List<ushort> Passives,
        Dictionary<ushort, ushort> MasteryEffects, ushort ExtraPassivePoints, IReadOnlyList<string> Warnings)
    {
        public CharacterSaves.Save ToSave(PathOfBuildingItems.Result items) => new(items.Inventories,
            SkillBar: items.SkillBar, Experience: Experience, Passives: Passives, MasteryEffects: MasteryEffects,
            ExtraPassivePoints: ExtraPassivePoints, Waypoints: [Town], Visited: [Town]);
    }

    const string Town = "1_1_town";

    public static Progress Prepare(PathOfBuildingBuild build, string folder = "data",
        Func<IReadOnlyDictionary<uint, uint[]>>? graphProvider = null)
    {
        if (!Enum.IsDefined(build.Class) || build.Level is < 1 or > 100)
            throw new ArgumentException("The build must use a supported character class and a level from 1 to 100.");
        var tree = build.Tree;
        var warnings = new List<string>();
        if (tree.ClassId != (int)build.Class)
            throw new ArgumentException("The build's class does not match its selected passive tree.");
        if (tree.Version is { Length: > 0 } version && version is not "3_29" and not "3.29")
            throw new ArgumentException($"The selected passive tree targets {version}; this server requires 3_29.");
        if (tree.AscendancyId != 0 || tree.SecondaryAscendancyId != 0
            || !string.IsNullOrEmpty(build.Ascendancy) && !build.Ascendancy.Equals("None", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Skipped ascendancy selection '{build.Ascendancy ?? tree.AscendancyId.ToString()}' and secondary ascendancy; the character will be unascended.");
        if (tree.Jewels.Count > 0)
            warnings.Add($"Skipped {tree.Jewels.Count} passive-tree jewel(s), including their modifiers and cluster expansions.");
        if (tree.HasOverrides)
            warnings.Add("Skipped passive-tree overrides; retained nodes use this client's ordinary passive effects.");
        foreach (var (label, value) in new[] { ("bandit reward", build.Bandit),
            ("major Pantheon", build.PantheonMajor), ("minor Pantheon", build.PantheonMinor) })
            if (NonDefault(value)) warnings.Add($"Skipped unsupported {label} '{value}'.");

        // Character integer IDs are the PoB class IDs, whereas PassiveSkills.Characters stores data-table rows.
        using var characters = ReadTable(folder, "Characters");
        int classRow = -1, row = 0;
        foreach (var character in characters.RootElement.EnumerateArray())
        {
            if (character.GetProperty("IntegerId").GetInt32() == (int)build.Class) classRow = row;
            row++;
        }
        if (classRow < 0) throw new ArgumentException($"The extracted Characters table does not contain {build.Class}.");

        using var experience = ReadTable(folder, "ExperienceLevels");
        uint? xp = null;
        foreach (var entry in experience.RootElement.EnumerateArray())
            if (entry.GetProperty("Level").GetInt32() == build.Level)
                xp = entry.GetProperty("Experience").GetUInt32();
        if (xp is null || build.Level > 1 && xp == 0)
            throw new ArgumentException($"The extracted ExperienceLevels table has no valid experience for level {build.Level}.");

        var passives = new List<ushort>();
        var masteries = new Dictionary<ushort, ushort>();
        if (tree.Version is null or "")
            warnings.Add("The build does not identify its passive-tree version; all selected node and mastery IDs were checked against 3.29 data.");
        var passiveTree = new PassiveTree(folder);
        using var passiveData = ReadTable(folder, "PassiveSkills");
        var metadata = new Dictionary<uint, JsonElement>();
        foreach (var node in passiveData.RootElement.EnumerateArray())
        {
            if (node.GetProperty("PassiveSkillGraphId").TryGetUInt32(out uint id)) metadata.TryAdd(id, node);
        }
        var seen = new HashSet<uint>();
        foreach (uint id in tree.Nodes)
        {
            if (!seen.Add(id)) throw new ArgumentException($"Passive {id} occurs more than once in the selected tree.");
            if (id > ushort.MaxValue || !metadata.TryGetValue(id, out var raw)
                || passiveTree.Find((ushort)id) is not { } node)
            { warnings.Add($"Skipped passive {id}: unavailable in this client's ordinary tree (including generated cluster nodes)."); continue; }
            if (node.IsAscendancy)
            { warnings.Add($"Skipped ascendancy passive {id} ({node.Name})."); continue; }
            if (Flag(raw, "IsExpansion") || Flag(raw, "IsProxyPassive") || Flag(raw, "IsAnointmentOnly")
                || raw.TryGetProperty("SkillType", out var skillType) && skillType.GetInt32() != 0
                || raw.TryGetProperty("DescendancyKey", out var descendancy) && descendancy.ValueKind == JsonValueKind.Number)
            { warnings.Add($"Skipped unsupported special passive {id} ({node.Name})."); continue; }
            if (node.IsStart)
            {
                if (!raw.GetProperty("Characters").EnumerateArray().Any(c => c.GetInt32() == classRow))
                    throw new ArgumentException($"The selected tree contains another class's starting node ({id}).");
                // The native client supplies the class start implicitly; it spends no passive skill point.
                continue;
            }
            passives.Add((ushort)id);
        }
        foreach (var (nodeId, effectId) in tree.Masteries)
        {
            if (!passives.Contains(nodeId))
            { warnings.Add($"Skipped mastery {effectId} on omitted passive {nodeId}."); continue; }
            if (passiveTree.Find(nodeId) is not { MasteryGroup: >= 0 })
                throw new ArgumentException($"Mastery effect {effectId} is not valid for allocated passive {nodeId} in 3.29.");
            if (passiveTree.FindEffect(effectId) is not { } effect
                || effect.Hash != effectId || !passiveTree.IsEffectFor(nodeId, effect)
                || masteries.ContainsValue(effectId))
            {
                passives.Remove(nodeId);
                warnings.Add($"Skipped mastery passive {nodeId}: effect {effectId} is unavailable, incompatible or already selected.");
                continue;
            }
            masteries.Add(nodeId, effectId);
        }
        foreach (ushort nodeId in passives)
            if (passiveTree.Find(nodeId) is { MasteryGroup: >= 0 } && !masteries.ContainsKey(nodeId))
                warnings.Add($"Skipped mastery passive {nodeId}: no supported mastery effect was selected.");
        passives.RemoveAll(id => passiveTree.Find(id) is { MasteryGroup: >= 0 } && !masteries.ContainsKey(id));

        if (passives.Count > 0)
        {
            uint? classStart = metadata.Values.FirstOrDefault(n => n.GetProperty("Characters").EnumerateArray()
                .Any(c => c.GetInt32() == classRow)) is { ValueKind: JsonValueKind.Object } start
                ? start.GetProperty("PassiveSkillGraphId").GetUInt32() : null;
            if (classStart is null) throw new ArgumentException("The extracted passive tree has no starting node for this class.");
            var graph = (graphProvider ?? PathOfBuildingPassiveGraph.Load)();
            if (!graph.ContainsKey(classStart.Value))
                throw new ArgumentException("The copied client's passive graph has no starting node for this class.");
            var connected = PathOfBuildingPassiveGraph.Connected(classStart.Value, passives,
                id => passiveTree.Find(id) is { MasteryGroup: >= 0 }, graph);
            foreach (ushort id in passives.Where(id => !connected.Contains(id)))
                warnings.Add($"Skipped disconnected passive {id} ({passiveTree.Find(id)?.Name}); it has no retained path to the class start.");
            passives.RemoveAll(id => !connected.Contains(id));
            foreach (ushort id in masteries.Keys.Where(id => !connected.Contains(id)).ToArray()) masteries.Remove(id);
        }

        int requiredQuestPoints = Math.Max(0, passives.Count - (build.Level - 1) - passiveTree.PointsGranted(passives));
        if (requiredQuestPoints > 24)
            throw new ArgumentException($"The selected tree requires {requiredQuestPoints} extra passive points; the supported quest budget is 24.");
        if (requiredQuestPoints > 0)
            warnings.Add($"Imported {requiredQuestPoints} extra passive point(s) needed by the selected tree; campaign quests were not completed.");
        warnings.Add("The character starts in Lioneye's Watch. PoB calculations, configuration, quest progress and unsupported gameplay mechanics are not imported.");
        return new(build.Class, build.Level, xp.Value, passives, masteries, (ushort)requiredQuestPoints, warnings);
    }

    public static Character Create(SaveDatabase db, string accountName, string characterName, Progress progress,
        CharacterSaves.Save save, string league = "Standard", LeagueCatalog? leagues = null, string? legacyDataFolder = null)
    {
        var selectedLeague = (leagues ?? LeagueCatalog.FromEnvironment()).Find(league)
            ?? throw new ArgumentException($"League '{league}' is not offered by this server.");
        if (selectedLeague.Ruthless)
            throw new ArgumentException("Ruthless import requires separate native passive, item and gem validation and is not supported.");
        if (save.Experience != progress.Experience || save.Passives is null || !save.Passives.SequenceEqual(progress.Passives)
            || save.ExtraPassivePoints != progress.ExtraPassivePoints)
            throw new ArgumentException("The prepared character progress and save do not match.");
        string json = JsonSerializer.Serialize(save, CharacterSaves.Opts);
        return db.Atomically(() =>
        {
            var store = new SqliteAccountStore(db);
            var account = store.FindAccount(accountName)
                ?? throw new ArgumentException($"Account '{accountName}' does not exist. Create it before importing a character.");
            if (db.Get(SaveDatabase.Table.Characters, characterName) is not null)
                throw new ArgumentException($"A save already exists for '{characterName}'; import will not overwrite it.");
            foreach (string legacyFolder in new[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path))!, "saves"),
                         Path.GetFullPath(Path.Combine(legacyDataFolder ?? "data", "saves")) }.Distinct(StringComparer.OrdinalIgnoreCase))
                if (Directory.Exists(legacyFolder) && Directory.EnumerateFiles(legacyFolder, "*.json")
                    .Any(file => Path.GetFileNameWithoutExtension(file).Equals(characterName, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"A legacy save already exists for '{characterName}'; start the Gateway to migrate saves before importing.");
            var accounts = new AccountService(store);
            var result = accounts.CreateCharacter(account.Id, characterName, progress.Class, selectedLeague.Name);
            if (!result.Ok) throw new ArgumentException($"Cannot create '{characterName}': {result.Error}.");
            var character = result.Value! with { Level = progress.Level, AreaId = Town, LastTown = Town };
            store.UpdateCharacter(character);
            db.Put(SaveDatabase.Table.Characters, characterName, json);
            return character;
        });
    }

    static JsonDocument ReadTable(string folder, string table)
    {
        string path = Path.Combine(folder, "tables", table + ".json");
        if (!File.Exists(path)) throw new ArgumentException($"Missing game data: {path}. Run scripts/dev/extract-data.ps1 first.");
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc;
        doc.Dispose();
        throw new ArgumentException($"The extracted {table} table is invalid; extract the game data again.");
    }

    static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    static bool NonDefault(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Equals("None", StringComparison.OrdinalIgnoreCase);
}

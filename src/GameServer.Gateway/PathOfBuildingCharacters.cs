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
        if (tree.ClassId != (int)build.Class)
            throw new ArgumentException("The build's class does not match its selected passive tree.");
        if (tree.Version is { Length: > 0 } version && version is not "3_29" and not "3.29")
            throw new ArgumentException($"The selected passive tree targets {version}; this server requires 3_29.");
        if (tree.AscendancyId != 0 || tree.SecondaryAscendancyId != 0
            || !string.IsNullOrEmpty(build.Ascendancy) && !build.Ascendancy.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ascendancy import is not supported by this server's ascendancy lifecycle. " +
                "Select an unascended build; no character was created.");
        if (tree.Jewels.Count > 0 || tree.HasOverrides)
            throw new ArgumentException("Passive-tree jewels and overrides cannot be imported faithfully by this server.");
        if (NonDefault(build.Bandit) || NonDefault(build.PantheonMajor) || NonDefault(build.PantheonMinor))
            throw new ArgumentException("Named bandit and Pantheon rewards are not implemented by this server. " +
                "Choose None before importing; those build stat sources cannot be silently omitted.");

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
        var warnings = new List<string>();
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
                throw new ArgumentException($"Passive {id} does not exist in the extracted 3.29 passive tree.");
            if (node.IsAscendancy)
                throw new ArgumentException($"Passive {id} ({node.Name}) requires the unsupported ascendancy lifecycle.");
            if (Flag(raw, "IsExpansion") || Flag(raw, "IsProxyPassive") || Flag(raw, "IsAnointmentOnly")
                || raw.TryGetProperty("SkillType", out var skillType) && skillType.GetInt32() != 0
                || raw.TryGetProperty("DescendancyKey", out var descendancy) && descendancy.ValueKind == JsonValueKind.Number)
                throw new ArgumentException($"Passive {id} ({node.Name}) is a special tree node that cannot be imported faithfully.");
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
            if (!passives.Contains(nodeId) || passiveTree.FindEffect(effectId) is not { } effect
                || effect.Hash != effectId || !passiveTree.IsEffectFor(nodeId, effect))
                throw new ArgumentException($"Mastery effect {effectId} is not valid for allocated passive {nodeId} in 3.29.");
            masteries.Add(nodeId, effectId);
        }
        foreach (ushort nodeId in passives)
            if (passiveTree.Find(nodeId) is { MasteryGroup: >= 0 } && !masteries.ContainsKey(nodeId))
                throw new ArgumentException($"Allocated mastery {nodeId} has no selected mastery effect.");
        if (masteries.Values.Distinct().Count() != masteries.Count)
            throw new ArgumentException("The same mastery effect cannot be selected more than once.");

        if (passives.Count > 0)
        {
            uint? classStart = metadata.Values.FirstOrDefault(n => n.GetProperty("Characters").EnumerateArray()
                .Any(c => c.GetInt32() == classRow)) is { ValueKind: JsonValueKind.Object } start
                ? start.GetProperty("PassiveSkillGraphId").GetUInt32() : null;
            if (classStart is null) throw new ArgumentException("The extracted passive tree has no starting node for this class.");
            PathOfBuildingPassiveGraph.Validate(classStart.Value, passives,
                id => passiveTree.Find(id) is { MasteryGroup: >= 0 }, (graphProvider ?? PathOfBuildingPassiveGraph.Load)());
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

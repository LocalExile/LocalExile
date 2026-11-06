using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;
using Microsoft.Data.Sqlite;

namespace PobImport;

static class Entry
{
    sealed record Input(string Code, string Name, string Database, string Data, string? SourceFile = null, bool AllowPartial = false, bool PrepareOnly = false, string? RecordJson = null);
    static readonly Dictionary<string, ItemCatalog> Catalogs = new(StringComparer.OrdinalIgnoreCase);

    // Inventories.dat rows the build replaces: equipment, flasks, the weapon swap and the tree's jewel pane.
    static readonly byte[] Replaced = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 15, 102, PassiveJewels];
    const byte PassiveJewels = 23;

    public static int Main()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var input = JsonSerializer.Deserialize<Input>(Console.In.ReadToEnd().TrimStart((char)0xFEFF, (char)0xEF, (char)0xBB, (char)0xBF),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("empty input");
            if (!input.PrepareOnly) throw new ArgumentException("The module converter only prepares builds; the Gateway applies them.");
            Run(input);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ОШИБКА: " + e.Message);
            return 1;
        }
    }

    static void Run(Input input)
    {
        string data = Path.GetFullPath(input.Data), database = Path.GetFullPath(input.Database);
        var warnings = new List<string>();
        var build = NativeSource.Parse(input.Code.Trim(), input.SourceFile);
        // Relics (foil uniques) are ordinary uniques for the server.
        build = build with { Items = [.. build.Items.Select(i => i with { Text = string.Join('\n', Regex.Replace(i.Text.Replace("Rarity: RELIC", "Rarity: UNIQUE"), @"(?m)^Superior (?=\S)", "").Split('\n').Where(l => !l.TrimStart().StartsWith("{vestigial}") && !Regex.IsMatch(l.Trim(), @"^(Requires Class \w+|Foil Unique \(.*\)|Ward: \d+|WardBasePercentile: [\d.]+|Memory Strands: \d+)$"))) })] };

        NativeSource.Begin(input.Code.Trim(), build, input.SourceFile);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 10 }.ToString());
        connection.Open();
        string name = input.Name.Trim();
        var record = JsonNode.Parse(input.RecordJson ?? throw new ArgumentException("Missing character record"))!.AsObject();
        var save = new JsonObject();

        // Passives, masteries, tattoos and level (evryBuild), with the unique catalogue completing uniques.
        PathOfBuildingCharacters.Progress progress;
        try { progress = PathOfBuildingCharacters.Prepare(build, data, () => NativeGraph.Load(data)); }
        catch (ArgumentException e)
        {
            // Never give up on the tree: retry without masteries and tattoos, then with ordinary passives only.
            warnings.Add("Дерево: " + e.Message + " — повтор без мастерий и тату.");
            build = build with { Tree = build.Tree with { Masteries = new Dictionary<ushort, ushort>(), Overrides = [] } };
            progress = PathOfBuildingCharacters.Prepare(build, data, () => NativeGraph.Load(data));
        }
        warnings.AddRange(progress.Warnings.Where(w => !w.StartsWith("Skipped ascendancy") && !w.Contains("passive-tree jewel")
            && !Regex.IsMatch(w, @"^Skipped passive 6\d{4}: unavailable") && !w.Contains("Jewel Socket)")
            && !w.StartsWith("The character starts in")));
        if (!Catalogs.TryGetValue(data, out var catalog)) Catalogs[data] = catalog = new ItemCatalog(data);
        Item Finish(PathOfBuildingItem definition, ItemCatalog.BaseType type, Item item, List<string> own) =>
            !item.NativeMods && ItemSend.Definition(connection, ItemSend.Title(definition.Text), type, item) is { } unique
                ? ItemSend.Completed(item, type, unique, catalog.Mods, own) : item;
        // Abyss jewels sit in their host item's Abyss sockets: PoB lists them as "<slot> Abyssal Socket N" slots, which
        // the converter refuses. Take them out here and put them into the host's Nth Abyss socket after it is built.
        var abyssal = build.Slots.Where(x => x.ItemId != 0 && x.Name.Contains(" Abyssal Socket ")).ToList();
        build = build with { Slots = [.. build.Slots.Where(x => !x.Name.Contains(" Abyssal Socket "))] };
        // Skip whatever the converter refuses (an item slot or a skill group) and keep the rest.
        PathOfBuildingItems.Result equipment;
        for (int attempt = 0; ; attempt++)
        {
            try { equipment = PathOfBuildingItems.Materialize(build, catalog, data, Finish); break; }
            catch (ArgumentException e) when (attempt < 40)
            {
                var bad = Regex.Matches(e.Message, @"^- (.+?): (.*)$", RegexOptions.Multiline).Select(m => (Where: m.Groups[1].Value, Why: m.Groups[2].Value)).ToList();
                if (bad.Count == 0) throw;
                foreach (var (where, why) in bad) warnings.Add($"Пропущено ({where}): {why}");
                var slots = bad.Where(b => !b.Where.StartsWith("Skills (")).Select(b => b.Where.StartsWith("Weapon set") ? "" : b.Where).ToHashSet();
                // A hand conflict names the weapon set: drop its off hand.
                foreach (var b in bad.Where(b => b.Where.StartsWith("Weapon set"))) slots.Add("Weapon 2" + b.Where["Weapon set".Length..]);
                var groups = bad.Where(b => b.Where.StartsWith("Skills (")).Select(b => b.Where[8..^1]).ToHashSet();
                build = build with
                {
                    Slots = [.. build.Slots.Where(x => !slots.Contains(x.Name))],
                    Skills = [.. build.Skills.Where(g => g.Slot is null || !groups.Contains(g.Slot))],
                };
            }
        }
        warnings.AddRange(equipment.Warnings);
        {
            var hostRows = new Dictionary<string, byte> { ["Body Armour"] = 1, ["Weapon 1"] = 2, ["Weapon 2"] = 3, ["Helmet"] = 4, ["Amulet"] = 5,
                ["Ring 1"] = 6, ["Ring 2"] = 7, ["Ring 3"] = 102, ["Gloves"] = 8, ["Boots"] = 9, ["Belt"] = 10, ["Weapon 1 Swap"] = 14, ["Weapon 2 Swap"] = 15 };
            var byId = build.Items.ToDictionary(i => i.Id);
            foreach (var slot in abyssal)
            {
                var m = Regex.Match(slot.Name, @"^(.+) Abyssal Socket (\d+)$");
                if (!m.Success || !hostRows.TryGetValue(m.Groups[1].Value, out byte row) || !byId.TryGetValue(slot.ItemId, out var definition)) continue;
                if (build.UseSecondWeaponSet) row = row switch { 2 => 14, 3 => 15, 14 => 2, 15 => 3, _ => row };
                if (!equipment.Inventories.TryGetValue(row, out var list) || list.Count == 0)
                { warnings.Add($"Пропущено ({slot.Name}): нет предмета-хозяина."); continue; }
                try
                {
                    var own = new List<string>();
                    var (type, item) = PathOfBuildingItems.ParseItem(definition, catalog, data, own);
                    item = Finish(definition, type, item, own);
                    warnings.AddRange(own);
                    var host = list[0];
                    var sockets = host.Item.Sockets ?? [];
                    int nth = int.Parse(m.Groups[2].Value), seen = 0, at = -1;
                    for (int k = 0; k < sockets.Count; k++)
                        if (sockets[k].Colour == SocketColour.Abyss && ++seen == nth) { at = k; break; }
                    if (at < 0) { warnings.Add($"Пропущено ({slot.Name}): у предмета нет {nth}-го абисс-сокета."); continue; }
                    var changed = sockets.ToList();
                    changed[at] = changed[at] with { Gem = CharacterSaves.ToSaved(item, catalog.Mods) };
                    list[0] = host with { Item = host.Item with { Sockets = changed } };
                }
                catch (Exception e) { warnings.Add($"Пропущено ({slot.Name}): {e.Message}"); }
            }
        }
        // Equipment: the same passive-naming lines (an amulet's anointment), matched to each slot's PoB item.
        {
            var rows = new Dictionary<string, byte> { ["Body Armour"] = 1, ["Weapon 1"] = 2, ["Weapon 2"] = 3, ["Helmet"] = 4, ["Amulet"] = 5,
                ["Ring 1"] = 6, ["Ring 2"] = 7, ["Ring 3"] = 102, ["Gloves"] = 8, ["Boots"] = 9, ["Belt"] = 10, ["Weapon 1 Swap"] = 14, ["Weapon 2 Swap"] = 15 };
            var texts = build.Items.ToDictionary(i => i.Id, i => i.Text);
            foreach (var slot in build.Slots.Where(x => x.ItemId != 0 && texts.ContainsKey(x.ItemId)))
            {
                byte row; byte x0 = 0;
                if (rows.TryGetValue(slot.Name, out byte r0)) row = build.UseSecondWeaponSet ? (byte)(r0 switch { 2 => 14, 3 => 15, 14 => 2, 15 => 3, _ => r0 }) : r0;
                else if (Regex.Match(slot.Name, @"^Flask ([1-5])$") is { Success: true } f) { row = 11; x0 = (byte)(f.Groups[1].Value[0] - '1'); }
                else continue;
                if (!equipment.Inventories.TryGetValue(row, out var list)) continue;
                for (int k = 0; k < list.Count; k++)
                    if (list[k].X == x0) list[k] = list[k] with { Item = PassiveHashMods(list[k].Item, texts[slot.ItemId], data) };
            }
        }

        var tree = new PassiveTree(data);
        var cells = JewelCells.Load(data);

        // Ascendancy: the selection by its Ascendancy.dat Id and every allocated ascendancy node except the free start.
        JsonObject? ascendancy = null;
        // Only the chosen ascendancy's own nodes (PassiveSkills.AscendancyKey), never its free start node: bloodline
        // and other trees' nodes make the Gateway refuse the whole save (AscendancyRuleException).
        var ascendancyRows = new Dictionary<ushort, (int Key, bool Start)>();
        using (var ps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveSkills.json"))))
            foreach (var r in ps.RootElement.EnumerateArray())
                if (r.TryGetProperty("AscendancyKey", out var key) && key.ValueKind == JsonValueKind.Number
                    && r.GetProperty("PassiveSkillGraphId").GetInt32() is int g and > 0 and <= ushort.MaxValue)
                    ascendancyRows.TryAdd((ushort)g, (key.GetInt32(), r.GetProperty("IsAscendancyStartingNode").GetBoolean()));
        int ascendancyRow = -1;
        using (var asc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "Ascendancy.json"))))
            ascendancyRow = asc.RootElement.EnumerateArray().Select((r, i) => (r, i)).FirstOrDefault(x =>
                string.Equals(x.r.GetProperty("Name").GetString(), build.Ascendancy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.r.GetProperty("Id").GetString(), build.Ascendancy, StringComparison.OrdinalIgnoreCase), (default, -1)).Item2;
        var ascendancyNodes = build.Tree.Nodes.Where(id => id <= ushort.MaxValue && ascendancyRows.TryGetValue((ushort)id, out var a)
                && a.Key == ascendancyRow && !a.Start).Select(id => (ushort)id).ToList();
        if (!string.IsNullOrWhiteSpace(build.Ascendancy) && !build.Ascendancy.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            using var asc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "Ascendancy.json")));
            var row = asc.RootElement.EnumerateArray().FirstOrDefault(r =>
                string.Equals(r.GetProperty("Name").GetString(), build.Ascendancy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.GetProperty("Id").GetString(), build.Ascendancy, StringComparison.OrdinalIgnoreCase));
            if (row.ValueKind != JsonValueKind.Object) warnings.Add($"Асцендант '{build.Ascendancy}' не найден в Ascendancy.json — пропущен.");
            else
                ascendancy = new JsonObject
                {
                    ["Selection"] = new JsonObject { ["Family"] = 0, ["Id"] = row.GetProperty("Id").GetString(), ["Revision"] = "3.29.3b" },
                    ["Nodes"] = new JsonArray([.. ascendancyNodes.Select(n => (JsonNode)n)]),
                    ["Awards"] = new JsonArray("normal", "cruel", "merciless", "eternal"),
                };
        }

        // Tree jewels: every PoB socket -> the PassiveJewels pane cell of that node (ordinary or cluster expansion).
        var definitions = build.Items.ToDictionary(i => i.Id);
        var jewels = new List<(int Cell, CharacterSaves.SavedItem Item, int? ClusterSize, string Text)>();
        foreach (var socket in build.Tree.Jewels)
        {
            if (!definitions.TryGetValue(socket.ItemId, out var definition)) continue;
            if (socket.NodeId > ushort.MaxValue || !cells.ByNode.TryGetValue((ushort)socket.NodeId, out int cell))
            { warnings.Add($"Джевел в узле {socket.NodeId}: ячейка не найдена — пропущен."); continue; }
            if (!build.Tree.Nodes.Contains(socket.NodeId)) continue; // PoB keeps jewels of unallocated sockets: they do nothing.
            try
            {
                var own = new List<string>();
                // PoB's own cluster properties: the layout comes from the enchantments, which stay.
                var text = string.Join('\n', definition.Text.Replace("\r", "").Split('\n')
                    .Where(l => !l.StartsWith("Cluster Jewel Skill:") && !l.StartsWith("Cluster Jewel Node Count:")
                        && !l.StartsWith("Added Small Passive Skills grant:") && !l.StartsWith("{crafted}Added Small Passive Skills grant:")));
                var (type, item) = PathOfBuildingItems.ParseItem(definition with { Text = text }, catalog, data, own);
                item = Finish(definition, type, item, own);
                warnings.AddRange(own);
                int? size = type.Name switch { "Small Cluster Jewel" => 0, "Medium Cluster Jewel" => 1, "Large Cluster Jewel" => 2, _ => null };
                var saved = CharacterSaves.ToSaved(item, catalog.Mods);
                saved = PassiveHashMods(saved, definition.Text, data);
                if (Regex.Match(definition.Text, @"Adds (\d+) Small Passive Skills? which grants? nothing") is { Success: true } voices)
                {
                    // Voices: the empty-passive variant is the count (1/3/5/7), not the roll evryBuild guessed.
                    string variant = voices.Groups[1].Value switch { "1" => "ExpansionJewelEmptyPassiveUnique__1", "3" => "ExpansionJewelEmptyPassiveUnique__2",
                        "5" => "ExpansionJewelEmptyPassiveUnique_3_", _ => "ExpansionJewelEmptyPassiveUnique__4" };
                    saved = saved with { Mods = [.. (saved.Mods ?? []).Select(m => m.Id.StartsWith("ExpansionJewelEmptyPassiveUnique")
                        ? m with { Id = variant, Values = [int.Parse(voices.Groups[1].Value)] } : m)] };
                }
                if (size is not null && !definition.Text.Contains("Rarity: UNIQUE") && Regex.IsMatch(definition.Text, @"Adds \d+ Passive Skills")) saved = FixCluster(saved, definition.Text, data, warnings);
                jewels.Add((cell, saved, size, definition.Text));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
            { warnings.Add($"Джевел '{ItemSend.Title(definition.Text)}' (ячейка {cell}): {e.Message}"); }
        }

        // Cluster passives: PoB's subgraph ids -> the client's expansion ids (Agents/docs/game/cluster-jewels.md).
        var templates = ClusterTemplates.Load(data);
        var clusterIds = new SortedSet<ushort>();
        var jewelByCell = jewels.ToDictionary(j => j.Cell);
        foreach (uint id in build.Tree.Nodes.Where(n => n >= 0x10000))
        {
            int index = (int)(id & 0xF), size = (int)(id >> 4 & 3), large = (int)(id >> 6 & 7), medium = (int)(id >> 9 & 3);
            if (index >= 12) continue; // PoB's mastery decoration (index 12) is not a client node.
            // A smaller jewel may sit in a larger socket, and medium index 0 adds nothing to PoB's id: walk the large
            // socket -> its medium cell -> that one's small cell and take the first that holds a jewel of this size.
            int? at = null;
            int lc = cells.Large.FirstOrDefault(c => cells.Index[c] == large, -1);
            var path = new List<int>();
            if (lc >= 0)
            {
                path.Add(lc);
                int mc = cells.Children(lc).FirstOrDefault(c => cells.Index[c] == medium, -1);
                if (mc >= 0) { path.Add(mc); int sc = cells.Children(mc).FirstOrDefault(-1); if (sc >= 0) path.Add(sc); }
            }
            foreach (int c in path)
                if (jewelByCell.TryGetValue(c, out var j) && j.ClusterSize == size) { at = c; break; }
            if (at is null) { warnings.Add($"Кластерный узел {id}: нет кластера в ячейке — пропущен."); continue; }
            clusterIds.Add(ClientId(cells, templates, at.Value, size, index));
        }
        // Sockets inside cluster jewels are ordinary PassiveSkills ids in PoB; the client counts them as cluster nodes.
        foreach (uint id in build.Tree.Nodes.Where(n => n <= ushort.MaxValue))
        {
            if (!cells.ByNode.TryGetValue((ushort)id, out int cell) || cells.Parent(cell) is not int parent) continue;
            if (!jewelByCell.TryGetValue(parent, out var host) || host.ClusterSize is not int hostSize) continue;
            var template = templates[hostSize];
            int position = -1;
            if (hostSize == 2 && SocketCount(host.Text) == 1) position = 6; // PoB/client: a single-socket large uses index 6.
            else
            {
                int[] order = [0, 2, 1];
                int k = Array.IndexOf(order, cells.Index[cell]);
                if (hostSize < 2) k = 0;
                if (k >= 0 && k < template.Socket.Length) position = template.Socket[k];
            }
            if (position < 0) { warnings.Add($"Сокет кластера {id}: позиция не найдена — пропущен."); continue; }
            clusterIds.Add(ClientId(cells, templates, parent, hostSize, position));
        }

        // Points: ordinary passives and cluster nodes share the budget (level - 1 + quest/extra points).
        int ordinary = progress.Passives.Count, granted = tree.PointsGranted(progress.Passives);
        int extra = Math.Max(0, ordinary + clusterIds.Count - (build.Level - 1) - granted - NativeProgress.AscendancyPoints(build, data));

        // Inventories: keep everything but the replaced rows; new item ids start above the kept ones.
        var kept = new JsonObject();
        uint maxId = 0;
        if (save["Inventories"] is JsonObject old)
            foreach (var (row, list) in old)
            {
                if (Replaced.Contains(byte.Parse(row))) continue;
                kept[row] = list!.DeepClone();
                foreach (var p in list!.AsArray()) maxId = Math.Max(maxId, p!["Id"]!.GetValue<uint>());
            }
        uint next = maxId + 1;
        foreach (var (row, placements) in equipment.Inventories)
            kept[row.ToString()] = new JsonArray([.. placements.Select(p => Placement(next++, p.X, p.Y, p.Item))]);
        if (jewels.Count > 0)
            kept[PassiveJewels.ToString()] = new JsonArray([.. jewels.OrderBy(j => j.Cell).Select(j => Placement(next++, (byte)j.Cell, 0, j.Item))]);

        save["Inventories"] = kept;
        save["SkillBar"] = JsonSerializer.SerializeToNode(equipment.SkillBar.ToDictionary(k => k.Key.ToString(), k => k.Value));
        save["Experience"] = progress.Experience;
        save["Passives"] = JsonSerializer.SerializeToNode(progress.Passives);
        save["MasteryEffects"] = progress.MasteryEffects.Count > 0 ? JsonSerializer.SerializeToNode(progress.MasteryEffects.ToDictionary(k => k.Key.ToString(), k => k.Value)) : null;
        save["Tattoos"] = progress.Tattoos is { Count: > 0 } t ? JsonSerializer.SerializeToNode(t.ToDictionary(k => k.Key.ToString(), k => k.Value)) : null;
        save["TattooKeystones"] = progress.TattooKeystones is { Count: > 0 } tk ? JsonSerializer.SerializeToNode(tk.ToDictionary(k => k.Key.ToString(), k => k.Value)) : null;
        save["ExtraPassivePoints"] = extra;
        save["ClusterPassives"] = clusterIds.Count > 0 ? new JsonArray([.. clusterIds.Select(i => (JsonNode)i)]) : null;
        save["MainAscendancy"] = ascendancy;
        save["PantheonMajor"] = Pantheon(data, build.PantheonMajor, warnings);
        save["PantheonMinor"] = Pantheon(data, build.PantheonMinor, warnings);
        // Session state that belongs to the old build.
        foreach (string field in new[] { "ActiveAuras", "Life", "Mana", "VitalsOwner", "Tutorial", "StartWeapon", "AttackInPlaceSlots", "SkillBarMask52" })
            save[field] = null;
        if (!string.IsNullOrWhiteSpace(build.Bandit) && !build.Bandit.Equals("None", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Бандит '{build.Bandit}' не переносится: сервер его не хранит.");

        record["Class"] = (int)build.Class;
        record["Level"] = build.Level;

        // Lines handled here (PassiveHashMods) or that are PoB labels, not modifiers.
        warnings.RemoveAll(w => Regex.IsMatch(w, @"modifier '(Passive Skills in Radius of .* can be Allocated|without being connected to your tree|Passage|Allocates .*)'"));
        // A skill an item modifier or a passive grants comes with that item or passive: there are no gems to socket.
        warnings.RemoveAll(w => w.Contains("item-granted skill source"));
        int items = equipment.Inventories.Sum(i => i.Value.Count);
        int gems = equipment.Inventories.SelectMany(i => i.Value).Sum(i => i.Item.Sockets?.Count(s => s.Gem is not null) ?? 0);
        Console.WriteLine($"Персонаж: {name} -> {build.Class} {build.Ascendancy}, уровень {build.Level}");
        if (NativeSource.SourcePath is { } sourcePath) Console.WriteLine($"Нативный снимок: {sourcePath}; обработано предметов: {NativeSource.ResolvedItems}.");
        Console.WriteLine($"Предметов: {items}, камней: {gems}, пассивок: {ordinary}, мастерий: {progress.MasteryEffects.Count}, " +
            $"асценд. узлов: {ascendancyNodes.Count}, джевелов на дереве: {jewels.Count}, узлов кластеров: {clusterIds.Count}, доп. очков: {extra}");
        if (warnings.Count > 0)
        {
            Console.WriteLine($"Предупреждения ({warnings.Distinct().Count()}):");
            foreach (string w in warnings.Distinct()) Console.WriteLine("  - " + w);
        }
        var losses = warnings.Where(w => w.StartsWith("Пропущено") || w.StartsWith("Skipped item") || w.StartsWith("Skipped gem")
            || w.StartsWith("Skipped skill group") || w.StartsWith("Skipped modifier") || w.StartsWith("Джевел") || w.StartsWith("Кластер") || w.StartsWith("Дерево")).Distinct().ToArray();
        if (losses.Length > 0 && !input.AllowPartial)
            throw new InvalidDataException($"Импорт с потерями запрещён; база не изменена: {string.Join("; ", losses)}");
        Console.WriteLine("POB_RESULT|" + new JsonObject { ["Record"] = record, ["Save"] = save,
            ["Summary"] = $"{build.Class}, уровень {build.Level}; предметов {items}, камней {gems}, пассивок {ordinary}",
            ["Warnings"] = JsonSerializer.SerializeToNode(warnings.Distinct().ToArray()) }.ToJsonString());
    }

    /// <summary>A cluster jewel's structure enchantment: JewelExpansionPassiveNodes holds the theme (the
    /// PassiveTreeExpansionSkills row of PoB's "Cluster Jewel Skill") and the passive count. evryBuild leaves both 0,
    /// which the Gateway reads as a cluster with no passives.</summary>
    static CharacterSaves.SavedItem FixCluster(CharacterSaves.SavedItem item, string text, string data, List<string> warnings)
    {
        string title = ItemSend.Title(text);
        string? skill = Regex.Match(text, @"^Cluster Jewel Skill:\s*(\S+)", RegexOptions.Multiline) is { Success: true } s ? s.Groups[1].Value : null;
        int count = Regex.Match(text, @"Adds (\d+) Passive Skills") is { Success: true } a ? int.Parse(a.Groups[1].Value)
            : Regex.Match(text, @"^Cluster Jewel Node Count:\s*(\d+)", RegexOptions.Multiline) is { Success: true } n ? int.Parse(n.Groups[1].Value) : 0;
        int theme = -1;
        if (skill is not null)
        {
            using var ps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveSkills.json")));
            int row = ps.RootElement.EnumerateArray().Select((r, i) => (r, i))
                .FirstOrDefault(x => x.r.GetProperty("Id").GetString() == skill, (default, -1)).Item2;
            using var ex = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveTreeExpansionSkills.json")));
            theme = ex.RootElement.EnumerateArray().Select((r, i) => (r, i))
                .FirstOrDefault(x => x.r.GetProperty("PassiveSkillsKey").GetInt32() == row && row >= 0, (default, -1)).Item2;
        }
        if (theme < 0) { warnings.Add($"Кластер '{title}': тема '{skill}' не найдена."); theme = 0; }
        if (count <= 0) warnings.Add($"Кластер '{title}': не найдено число пассивок.");
        var mods = (item.Mods ?? []).Where(m => m.Id != "JewelExpansionPassiveNodes").ToList();
        mods.Insert(0, new CharacterSaves.SavedMod("JewelExpansionPassiveNodes", GameServer.Core.Protocol.Instance.ModSection.Enchant, [theme, count]));
        return item with { Mods = mods };
    }

    /// <summary>Jewel lines that name a passive instead of a number: Impossible Escape's keystone and Forbidden
    /// Flame/Flesh's ascendancy notable. The client's stat value is the passive's PassiveSkillGraphId (as the
    /// Gateway's ImpossibleEscapeCatalogue writes it); evryBuild cannot read a name as a value and drops them.</summary>
    static CharacterSaves.SavedItem PassiveHashMods(CharacterSaves.SavedItem item, string text, string data)
    {
        var added = new List<CharacterSaves.SavedMod>();
        if (Regex.Match(text, @"Passive Skills in Radius of (.+?) can be Allocated") is { Success: true } escape
            && GraphIdByName(data, escape.Groups[1].Value.Trim(), ascendancy: false) is int keystone)
            added.Add(new("JewelUniqueAllocateDisconnectedPassivesAroundKeystone", ModSection.Explicit, [keystone, 800]));
        foreach (Match forbidden in Regex.Matches(text, @"Allocates (.+?) if you have the matching modifier on Forbidden (Flame|Flesh)"))
            if (GraphIdByName(data, forbidden.Groups[1].Value.Trim(), ascendancy: true) is int notable)
                added.Add(new(forbidden.Groups[2].Value == "Flesh" ? "PuzzlePieceCleansingFireUnique__1" : "PuzzlePieceGreatTangleUnique__1",
                    ModSection.Explicit, [notable]));
        // Anointment: "Allocates <notable>" alone on its line is GrantedPassive, an enchantment.
        foreach (Match anoint in Regex.Matches(text.Replace("\r", ""), @"^(?:\{[^}]*\})*Allocates ([^\n]+?)\s*$", RegexOptions.Multiline))
            if (!anoint.Groups[1].Value.Contains(" if you have ") && GraphIdByName(data, anoint.Groups[1].Value.Trim(), ascendancy: false, notable: true) is int granted)
                added.Add(new("GrantedPassive", ModSection.Enchant, [granted]));
        if (added.Count == 0) return item;
        var ids = added.Select(a => a.Id).ToHashSet();
        return item with { Mods = [.. (item.Mods ?? []).Where(m => !ids.Contains(m.Id)), .. added] };
    }

    static Dictionary<string, (int Graph, bool Ascendancy)>? passiveNames;
    static int? GraphIdByName(string data, string name, bool ascendancy, bool notable = false)
    {
        if (passiveNames is null)
        {
            passiveNames = new(StringComparer.OrdinalIgnoreCase);
            using var ps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveSkills.json")));
            foreach (var r in ps.RootElement.EnumerateArray())
            {
                string n = r.GetProperty("Name").GetString() ?? "";
                bool asc = r.TryGetProperty("AscendancyKey", out var a) && a.ValueKind == JsonValueKind.Number;
                bool keystone = r.TryGetProperty("IsKeystone", out var k) && k.ValueKind == JsonValueKind.True;
                bool isNotable = r.TryGetProperty("IsNotable", out var nb) && nb.ValueKind == JsonValueKind.True;
                int graph = r.GetProperty("PassiveSkillGraphId").GetInt32();
                if (n.Length == 0) continue;
                if (asc || keystone) passiveNames.TryAdd(n + (asc ? "|a" : "|k"), (graph, asc));
                if (isNotable && !asc) passiveNames.TryAdd(n + "|n", (graph, false));
            }
        }
        return passiveNames.TryGetValue(name + (notable ? "|n" : ascendancy ? "|a" : "|k"), out var found) ? found.Graph : null;
    }

    static JsonObject Placement(uint id, byte x, byte y, CharacterSaves.SavedItem item) => new()
    {
        ["Id"] = id, ["X"] = x, ["Y"] = y, ["Item"] = JsonSerializer.SerializeToNode(item),
    };

    /// <summary>The client's id of a cluster node: the cell's block base plus the ring position of the jewel's template
    /// index, halved in an expansion cell (Agents/docs/game/cluster-jewels.md, ClusterJewelLayout).</summary>
    static ushort ClientId(JewelCells cells, Dictionary<int, ClusterTemplates.Template> templates, int cell, int size, int index)
    {
        var template = templates[size];
        int start = size < cells.Starts[cell].Length ? cells.Starts[cell][size] : 0;
        int position = (index + start) % template.Total;
        return (ushort)(cells.Base(cell) + (cells.Size[cell] == 2 ? position : position / 2));
    }

    static int SocketCount(string text) =>
        Regex.Match(text, @"Adds (\d+) Jewel Socket", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 2;

    static string? Pantheon(string data, string? god, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(god) || god.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PantheonPanelLayout.json")));
        foreach (var row in doc.RootElement.EnumerateArray())
            if (string.Equals(row.GetProperty("Id").GetString(), god, StringComparison.OrdinalIgnoreCase)
                || (row.GetProperty("GodName1").GetString() ?? "").EndsWith(" " + god, StringComparison.OrdinalIgnoreCase))
                return row.GetProperty("Id").GetString();
        warnings.Add($"Пантеон '{god}' не найден — пропущен.");
        return null;
    }


}

/// <summary>The PassiveJewels pane cells (PassiveJewelSlots.json): node -> cell, sizes, parents, ClusterIndex, starts.</summary>
sealed class JewelCells
{
    public Dictionary<ushort, int> ByNode { get; } = [];
    public Dictionary<int, int> Size { get; } = [];
    public Dictionary<int, int> Index { get; } = [];
    public Dictionary<int, int[]> Starts { get; } = [];
    public Dictionary<int, int> ParentOf { get; } = [];
    public List<int> Large { get; } = [];

    public int? Parent(int cell) => ParentOf.TryGetValue(cell, out int p) ? p : null;
    public IEnumerable<int> Children(int cell) => ParentOf.Where(kv => kv.Value == cell).Select(kv => kv.Key).OrderBy(c => Index[c]);

    public int Base(int cell)
    {
        if (Parent(cell) is not int parent) return 39 * Index[cell];
        if (Parent(parent) is null) return 39 * Index[parent] + 9 * Index[cell] + 12;
        return Base(parent) + 6;
    }

    public static JewelCells Load(string data)
    {
        var cells = new JewelCells();
        using var slots = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveJewelSlots.json")));
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveSkills.json")));
        var rows = passives.RootElement.EnumerateArray().ToArray();
        int cell = -1;
        foreach (var row in slots.RootElement.EnumerateArray())
        {
            cell++;
            int passiveRow = row.GetProperty("Slot").GetInt32();
            if (passiveRow < 0 || passiveRow >= rows.Length || !rows[passiveRow].GetProperty("IsJewelSocket").GetBoolean()) continue;
            if (row.GetProperty("__col6").GetBoolean() && row.GetProperty("ReplacesSlot").ValueKind == JsonValueKind.Null) continue;
            int graphId = rows[passiveRow].GetProperty("PassiveSkillGraphId").GetInt32();
            cells.ByNode.TryAdd((ushort)graphId, cell);
            var size = row.GetProperty("ClusterJewelSize");
            cells.Size[cell] = size.ValueKind == JsonValueKind.Number ? size.GetInt32() : -1;
            cells.Index[cell] = row.TryGetProperty("ClusterIndex", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : 0;
            cells.Starts[cell] = row.TryGetProperty("StartIndices", out var st) && st.ValueKind == JsonValueKind.Array
                ? [.. st.EnumerateArray().Select(v => v.GetInt32())] : [];
            var replaces = row.GetProperty("ReplacesSlot");
            if (replaces.ValueKind == JsonValueKind.Number) cells.ParentOf[cell] = replaces.GetInt32();
            else if (cells.Size[cell] == 2) cells.Large.Add(cell);
        }
        return cells;
    }
}

static class ClusterTemplates
{
    public sealed record Template(int[] Socket, int Total);

    public static Dictionary<int, Template> Load(string data)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "PassiveTreeExpansionJewels.json")));
        return doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("PassiveTreeExpansionJewelSizesKey").GetInt32(),
            r => new Template([.. r.GetProperty("SocketIndices").EnumerateArray().Select(v => v.GetInt32())], r.GetProperty("TotalIndices").GetInt32()));
    }
}

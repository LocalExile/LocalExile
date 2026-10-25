using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// NPCs (Agents/docs/protocol/17-npcs.md): NPCs.dat rows from the extracted tables with their object type's deserialized
/// components (data/object-types.json), and where each town's NPCs stand. The client files do not place town NPCs (the
/// town room has only its entrance points and the tiles only the waypoint and area transition), so the cells are ours.
/// </summary>
sealed class NpcCatalog
{
    /// <summary>An NPCs.dat row. <paramref name="ShopRow"/>: the NPCShop row of its ShopSet (NPCShopSets.Shop), −1 for none.</summary>
    public sealed record Npc(int Row, string Id, string Name, IReadOnlyList<string>? Components, StateMachineData? StateMachine,
        byte TransitionState, int ShopRow = -1)
    {
        public bool Supported => Components is { } c && NpcObject.CanSerialize(c, StateMachine is not null);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    /// <summary>An NPCTalk row: the NPC's dialogue option <paramref name="Index"/> (column 1, what C→S 0x82 carries), its
    /// NPCTalkCategory Id (Gossip, Quests, BuyItems, SellItems, IdentifyItems, RespecPassivePointsForGold …), script and
    /// NPCTextAudio rows.</summary>
    public sealed record Talk(int Row, int Npc, int Index, string Option, string Category, string Script, IReadOnlyList<int> TextAudio)
    {
        /// <summary>A SelectHideout row's script opens the guild's list: <c>SendOpenScreen( HideoutSelectionWindow, 1 )</c>
        /// (the character's list has no argument or 0).</summary>
        public bool OpensGuildHideouts => System.Text.RegularExpressions.Regex.IsMatch(Script, @"HideoutSelectionWindow\s*,\s*1\s*\)");
        public IReadOnlyList<string> RequiredFlags { get; init; } = [];
        public IReadOnlyList<string> ExcludedFlags { get; init; } = [];
        public string? StartFlag { get; init; }
        public string? FinishFlag { get; init; }
        public int? RewardOffer { get; init; }
        public bool Available(IReadOnlySet<string> flags) => RequiredFlags.All(flags.Contains) && !ExcludedFlags.Any(flags.Contains);
    }

    /// <summary>An NPC standing in a town: its NPCs.dat Id, cell and facing (radians).</summary>
    public sealed record Placement(string Id, int X, int Y, float Rotation);

    // Lioneye's Watch (spawn (197, 261), waypoint (256, 169), gate to The Coast (388, 231)): where they stand in the real
    // game, measured on its minimap (17-npcs.md §2: the minimap's projection fitted on known cells, under one cell of
    // error). Facing in radians (Positioned +0x29c), set live with /npc_face.
    static readonly Dictionary<ushort, Placement[]> Towns = new()
    {
        [AreaLayouts.LioneyesWatch] =
        [
            new("Metadata/NPC/Act1/Tarkleigh", 388, 182, 4.0f),
            new("Metadata/NPC/Act1/Nessa", 346, 271, 4.0f),
            new("Metadata/NPC/Act1/Bestel", 361, 246, 4.0f),
        ],
        // The Azurite Mine Encampment (Delve_Hub, HASH16 0x8b9b): Niko by the waypoint (333, 402) and the arrival
        // (330, 420); our own cell (2026-10-21). The hideout Niko's row: the hub's own (DelveMinerHub) lists no Sell Items.
        [StashCatalog.DelveHub] = [new("Metadata/NPC/League/Delve/DelveMinerHideout", 342, 410, 4.0f)],
    };

    public IReadOnlyList<Npc> Rows { get; }
    public string? Error { get; }
    readonly Dictionary<string, Npc> _byId;
    readonly Dictionary<(int Npc, int Index), Talk> _talk = [];

    NpcCatalog(List<Npc> rows, string? error, IEnumerable<Talk>? talk = null)
    {
        Rows = rows;
        Error = error;
        foreach (var t in talk ?? []) _talk.TryAdd((t.Npc, t.Index), t);
        _byId = rows.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public static NpcCatalog Load(string folder = "data")
    {
        try { return Read(folder); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new([], $"NPCs: {ex.Message}");
        }
    }

    /// <summary>The NPCTalk row an NPC's option index (C→S 0x82) stands for.</summary>
    public Talk? TalkOf(Npc npc, int index) => _talk.GetValueOrDefault((TalkSource(npc).Row, index));

    public bool HasAvailableReward(Npc npc, int offer, IReadOnlySet<string> flags) =>
        _talk.Values.Any(t => t.Npc == TalkSource(npc).Row && t.RewardOffer == offer && t.Available(flags));

    /// <summary>The NPC whose NPCTalk rows (and shop) this one uses: itself, or for a copy without rows of its own (the
    /// hideout Kirac, NPCs.dat row 412) the first row of the same name that has them (Kirac, 410).</summary>
    public Npc TalkSource(Npc npc) => _talk.Keys.Any(k => k.Npc == npc.Row) ? npc
        : Rows.FirstOrDefault(r => r.Name == npc.Name && _talk.Keys.Any(k => k.Npc == r.Row)) ?? npc;

    /// <summary>The town's NPCs, or none.</summary>
    // Hideouts: Nessa, at the cell the user picked in the Lush Hideout (2026-10-20); the real game has no town NPCs in
    // hideouts. Niko and Kirac (their hideout NPCs.dat rows) stand to her right and left on screen (2026-10-21): screen
    // right is +x / -y on the grid.
    static readonly Placement[] Hideout =
    [
        new("Metadata/NPC/Act1/Nessa", 256, 351, 4.0f),
        new("Metadata/NPC/League/Delve/DelveMinerHideout", 263, 344, 4.0f),
        new("Metadata/NPC/Missions/Hideout/Helena", 270, 337, 4.0f),
        new("Metadata/NPC/Epilogue/KiracHideout", 249, 358, 4.0f),
    ];

    public static IReadOnlyList<Placement> HideoutNpcs => Hideout;

    public static IReadOnlyList<Placement> In(ushort areaHash) =>
        Towns.GetValueOrDefault(areaHash) ?? (HideoutCatalog.ByArea(areaHash) is not null ? Hideout : []);

    /// <summary>An NPC by row, Id or name (exact name first, then the first Id or name containing the text).</summary>
    public Npc? Find(string query)
    {
        if (int.TryParse(query, out int row)) return row >= 0 && row < Rows.Count ? Rows[row] : null;
        if (_byId.TryGetValue(query, out var byId)) return byId;
        return Rows.FirstOrDefault(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? Rows.FirstOrDefault(r => r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    static NpcCatalog Read(string folder)
    {
        string table = Path.Combine(folder, "tables", "NPCs.json");
        if (!File.Exists(table)) return new([], $"{table} is missing: extract the game tables (Tools/PoEDataExtractor)");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var stateMachines = new Dictionary<string, StateMachineData>();
        var transitionStates = new Dictionary<string, byte>();
        string types = Path.Combine(folder, "object-types.json");
        if (File.Exists(types))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(types));
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                string type = t.GetProperty("type").GetString()!;
                if (!type.StartsWith("Metadata/NPC/", StringComparison.OrdinalIgnoreCase)) continue;
                components[type] = [.. t.GetProperty("components").EnumerateArray().Take(t.GetProperty("read").GetInt32()).Select(c => c.GetString()!)];
                if (t.TryGetProperty("shared_states", out var ss) && t.TryGetProperty("shared_timers", out var st))
                    stateMachines[type] = new([.. ss.EnumerateArray().Select(e => e[1].GetInt32())], st.GetArrayLength(),
                        [.. ss.EnumerateArray().Select(e => e[0].GetString() ?? "")]);
                if (t.TryGetProperty("transitionable", out var tr) && tr.TryGetProperty("num_states", out var ns))
                    transitionStates[type] = (byte)Math.Clamp(ns.GetInt32(), 1, 127);
            }
        }
        var shopOfSet = new List<int>();
        string sets = Path.Combine(folder, "tables", "NPCShopSets.json");
        if (File.Exists(sets))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(sets)))
                shopOfSet = [.. doc.RootElement.EnumerateArray().Select(s => s.GetProperty("Shop").ValueKind == JsonValueKind.Number ? s.GetProperty("Shop").GetInt32() : -1)];
        var rows = new List<Npc>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(table)))
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                // Without object-types.json, assume the base NPC list.
                IReadOnlyList<string>? list = components.Count == 0 ? NpcObject.BaseComponents : components.GetValueOrDefault(id);
                int set = r.GetProperty("ShopSet").ValueKind == JsonValueKind.Number ? r.GetProperty("ShopSet").GetInt32() : -1;
                rows.Add(new Npc(rows.Count, id, r.GetProperty("Name").GetString() ?? "", list,
                    stateMachines.GetValueOrDefault(id), transitionStates.GetValueOrDefault(id, (byte)1),
                    set >= 0 && set < shopOfSet.Count ? shopOfSet[set] : -1));
            }
        return new(rows, null, ReadTalk(folder));
    }

    static List<Talk> ReadTalk(string folder)
    {
        string table = Path.Combine(folder, "tables", "NPCTalk.json"), categories = Path.Combine(folder, "tables", "NPCTalkCategory.json");
        if (!File.Exists(table) || !File.Exists(categories)) return [];
        List<string> names;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(categories)))
            names = [.. doc.RootElement.EnumerateArray().Select(c => c.GetProperty("Id").GetString() ?? "")];
        string flagsFile = Path.Combine(folder, "tables", "QuestFlags.json");
        List<string> flags = [];
        if (File.Exists(flagsFile))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(flagsFile)))
                flags = [.. doc.RootElement.EnumerateArray().Select(f => f.GetProperty("Id").GetString() ?? "")];
        string? Flag(int row) => row >= 0 && row < flags.Count && flags[row] != "NEVER_SET" ? flags[row] : null;
        var talk = new List<Talk>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(table)))
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                int Int(string name) => r.GetProperty(name).ValueKind == JsonValueKind.Number ? r.GetProperty(name).GetInt32() : -1;
                int category = Int("Category");
                int OptionalInt(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
                string[] FlagList(string name) => r.TryGetProperty(name, out var v)
                    ? [.. v.EnumerateArray().Select(e => e.GetInt32()).Where(i => i >= 0 && i < flags.Count).Select(i => flags[i])] : [];
                talk.Add(new Talk(talk.Count, Int("NPCKey"), Int("__col1"), r.GetProperty("DialogueOption").GetString() ?? "",
                    category >= 0 && category < names.Count ? names[category] : "", r.GetProperty("Script").GetString() ?? "",
                    [.. r.GetProperty("NPCTextAudioKeys").EnumerateArray().Select(e => e.GetInt32())])
                {
                    RequiredFlags = FlagList("QuestFlag1"), ExcludedFlags = FlagList("QuestFlag2"),
                    StartFlag = Flag(OptionalInt("QuestFlag4")), FinishFlag = Flag(OptionalInt("QuestFlag5")),
                    RewardOffer = OptionalInt("QuestRewardOffersKey") is int offer and >= 0 ? offer : null
                });
            }
        return talk;
    }
}

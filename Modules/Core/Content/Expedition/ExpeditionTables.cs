using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Expedition;

/// <summary>
/// The numbers of the Expedition encounter. Every value says where it comes from: [data] a client table, [capture]
/// measured on two official Expedition map captures (levels 73 and 69, both Faction2), [estimate] a value neither
/// gives.
/// </summary>
public static class ExpeditionTables
{
    /// <summary>The four factions of ExpeditionFactions, in row order [data]: Druids of the Broken Circle, Black Scythe
    /// Mercenaries, Order of the Chalice, Knights of the Sun.</summary>
    public static readonly string[] FactionIds = ["Faction1", "Faction2", "Faction3", "Faction4"];

    public enum MarkerKind { Regular, Elite, Chest }
    public enum ChestTier { Low, Mid, High }

    // [capture] 99 markers in each encounter, of them 9 / 15 / 12 elite (runic) markers.
    public const int Markers = 99;
    public const int ElitesMin = 9;
    public const int ElitesMax = 15;
    // [capture] 9 chests, 5 explosives, 6 / 7 remnants: what the level's own row says, and what is used without it.
    public const int Chests = 9;
    public const int Explosives = 5;
    public const int Remnants = 6;

    /// <summary>
    /// How many chest markers, explosives and remnants this level's encounter has [data: <c>ExpeditionBalancePerLevel</c>,
    /// the level's row with <c>__col1</c> 0; a logbook's own row is <c>__col1</c> 1]. The columns are read off the
    /// captures: at level 69 the row is <c>0, 9, 10, 5, 6, 6, 72, …</c> and the capture has 9 chest markers, 5 explosives
    /// (its first <c>0x74 00</c> state 1 carries 5 left) and 6 remnants (its <c>0x74 03</c> total); at level 73 the row is
    /// <c>0, 9, 10, 5, 7, 7, 72, …</c> and the capture has 9, 5 and 7. So <c>__col2</c> = chests, <c>__col4</c> =
    /// explosives, <c>__col5</c> = remnants.
    /// </summary>
    public static (int Chests, int Explosives, int Remnants) Balance(int level) =>
        BalanceRows.TryGetValue(Math.Clamp(level, 1, 100), out var row) ? row : (Chests, Explosives, Remnants);

    // [capture] 27 chests, 6 of them Mid and none High.
    public const double ChestMidChance = 0.22;
    // [capture] 15 magic and 2 rare of the 197 dug-up monsters.
    public const double MagicChance = 0.076;
    public const double RareChance = 0.01;

    // [capture] distances from the detonator, in cells (the farthest marker is 137.6 cells away).
    public const double MarkerDistMin = 27;
    public const double MarkerDistMax = 125;
    public const double ChestDistMin = 36;
    public const double ChestDistMax = 82;
    public const double RemnantDistMin = 28;
    public const double RemnantDistMax = 100;
    public const double RemnantSepMin = 29;
    public const double MarkerSepMin = 4;

    /// <summary>
    /// The marker's model. The type carries none of its own (<c>object_types</c> <c>ExpeditionMarker</c>:
    /// <c>animated_object</c> is null), so the create names it in the Animated component or the marker is not drawn.
    /// <para>
    /// [capture] the 198 marker creates carry exactly these ten paths: a monster marker <c>monstermarker{,_02,_03}.ao</c>
    /// (54 / 59 / 43), an elite marker <c>elitemarker{,_02,_03}.ao</c> (7 / 9 / 8, and only these carry Positioned's 0x20
    /// scale, (1.5, 1.0, 1.0) in all 24) and a chest marker <c>chestmarker_signpost_{01,02,03}.ao</c> (4 / 7 / 2) or
    /// <c>chestmarker2_{02,03}.ao</c> (2 / 3). [estimate] the variant is drawn uniformly: the captures give the counts,
    /// not the rule.
    /// </para>
    /// </summary>
    public static string MarkerAo(MarkerKind kind, Random rng) => kind switch
    {
        MarkerKind.Elite => MarkerAoDir + EliteMarkerAo[rng.Next(EliteMarkerAo.Length)],
        MarkerKind.Chest => MarkerAoDir + ChestMarkerAo[rng.Next(ChestMarkerAo.Length)],
        _ => MarkerAoDir + MonsterMarkerAo[rng.Next(MonsterMarkerAo.Length)],
    };

    const string MarkerAoDir = "Metadata/Terrain/Doodads/Leagues/Expedition/";
    static readonly string[] MonsterMarkerAo = ["monstermarker.ao", "monstermarker_02.ao", "monstermarker_03.ao"];
    static readonly string[] EliteMarkerAo = ["elitemarker.ao", "elitemarker_02.ao", "elitemarker_03.ao"];
    static readonly string[] ChestMarkerAo =
        ["chestmarker_signpost_01.ao", "chestmarker_signpost_02.ao", "chestmarker_signpost_03.ao",
         "chestmarker2_02.ao", "chestmarker2_03.ao"];

    /// <summary>An elite marker's uniform scale [capture: Positioned flag 0x20 with (1.5, 1.0, 1.0) in all 24 elite-marker
    /// creates, and in no other Expedition create].</summary>
    public const float EliteMarkerScale = 1.5f;

    /// <summary>The remnant's model [data: <c>ExpeditionRelics</c> <c>CommonRemnant</c>'s <c>AOFile</c>; capture: all 13
    /// remnant creates name it in their Animated component].</summary>
    public static string RelicAo { get; private set; } = "";

    /// <summary>The TerrainPlugins id of ExpeditionPlugin [data: <c>__col1</c>, 0x086a], null without the table.</summary>
    public static ushort? PluginId { get; private set; }

    /// <summary>The Rulesets row of ExpeditionRules [data: row 116], null without the table.</summary>
    public static byte? RulesetRow { get; private set; }

    /// <summary>The ExpeditionPlugin state of an encounter of this faction: byte 0 is the ExpeditionFactions row shifted
    /// left by two, with bits 0 and 1 clear for the plain encounter row [capture: the Faction2 maps' 0x1d sends
    /// <c>04 00</c>]; see LevelGenerator.LeaguePluginFeature.</summary>
    public static byte[] PluginState(int faction) => [(byte)(faction << 2), 0x00];

    /// <summary>The faction an ExpeditionPlugin state names, or −1.</summary>
    public static int FactionOf(byte[] state) =>
        state.Length >= 1 && state[0] >> 2 is var faction && faction < FactionIds.Length ? faction : -1;

    /// <summary>The <c>ExtraTerrainFeatures</c> row the plugin's state adds to the area's generation: the encounter room
    /// <c>Metadata/Terrain/Leagues/Expedition/encounter.arm</c>, whose one server object line is the detonator.</summary>
    public const string Feature = "ExpeditionEncounter";

    /// <summary>The effect object the blast and the remnant's shockwave are drawn with.</summary>
    public const string EffectType = "Metadata/Effects/ServerEffect";

    // [capture] the .ao the official ServerEffect creates name: a placed explosive, a blast, a destroyed remnant's shockwave.
    const string EffectDir = "Metadata/Effects/Spells/monsters_effects/League_Expedition/";
    public const string PlaceAo = EffectDir + "dynamic_marker/place_explosive_audio.ao";
    public const string BlastAo = EffectDir + "mechanics/explosives/explosive_blast.ao";
    public const string ShockwaveAo = EffectDir + "mechanics/relics/shockwave.ao";

    /// <summary>The one base-data variable the official blast and shockwave creates carry (all 8 of them, the same bytes:
    /// hash a475c34a, flags 0, type 1, then 0a 01); what it names is not known.</summary>
    public static readonly byte[] EffectBaseData = [0xa4, 0x75, 0xc3, 0x4a, 0x00, 0x01, 0x0a, 0x01];

    /// <summary>
    /// What a create of this type carries: the components the client deserializes for it, in the type's own order, and
    /// the type's StateMachine defaults [data: client.db <c>object_types</c> / <c>object_type_shared_states</c>]. Empty
    /// for a type with no row, which <see cref="ExpeditionWire.Object"/> turns into no create rather than a guessed body.
    /// <para>
    /// A <c>Chests</c> row has no <c>.ot</c> of its own and takes its components from the type its <c>InheritsFrom</c>
    /// names: the dug chest <c>Metadata/Chests/LeaguesExpedition/LeagueFaction&lt;N&gt;{Low,Mid}</c> inherits from
    /// <c>Metadata/Chests/LeagueExpedition/ExpeditionChest</c>. The official 43-byte <c>LeagueFaction2Low</c> /
    /// <c>2Mid</c> bodies (12 creates in each capture) fit that read list exactly.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<string> Components, StateMachineData? States) ObjectType(string path) =>
        ObjectTypeRows.TryGetValue(path, out var found) ? found : ([], null);

    /// <summary>
    /// The chest a blast digs up, for this faction and tier [data: <c>Chests</c>
    /// <c>Metadata/Chests/LeaguesExpedition/LeagueFaction&lt;N&gt;&lt;Tier&gt;</c>; capture: both Faction2 captures create
    /// exactly <c>LeagueFaction2Low</c> and <c>LeagueFaction2Mid</c>]. Empty when the tables have no such row, and then no
    /// chest is created.
    /// </summary>
    public static string ChestPath(string factionId, ChestTier tier)
    {
        string id = $"Metadata/Chests/LeaguesExpedition/LeagueFaction{Math.Max(1, Array.IndexOf(FactionIds, factionId) + 1)}{tier}";
        return ChestPathRows.Contains(id) ? id : "";
    }

    /// <summary>
    /// One modifier a remnant can carry: its <c>Mods</c> HASH16 (what the create's ObjectMagicProperties names), its
    /// <c>ExpeditionRelicMods.Categories</c> row (0 monsters, 1 elites, 2 chests, 3 monster rarity), its values and its
    /// stats.
    /// </summary>
    public sealed record RelicMod(string Id, ushort Hash, int Category, IReadOnlyList<int> Values,
        IReadOnlyList<(uint Stat, int Value)> Stats)
    {
        public int GenerationType { get; init; }
        public IReadOnlyList<int> Families { get; init; } = [];
    }

    /// <summary>The modifiers a remnant can roll [data: <c>ExpeditionRelicMods</c>, 141 rows, each naming a <c>Mods</c>
    /// row]. Every one of those mods has a single value (Min = Max), so a remnant's values are the row's own.</summary>
    public static IReadOnlyList<RelicMod> RelicModPool { get; private set; } = [];

    /// <summary>
    /// A remnant's modifiers: one prefix and one suffix before scarab additions, without duplicate modifier families.
    /// Selection is uniform within each eligible generation pool; these weights remain server calibration.
    /// </summary>
    public static IReadOnlyList<RelicMod> RollRelicMods(Random rng) => RollRelicMods(rng, 0, 0);

    public static IReadOnlyList<RelicMod> RollRelicMods(Random rng, int extraPrefixes, int extraSuffixes)
    {
        var chosen = new List<RelicMod>();
        var families = new HashSet<int>();
        foreach (var (generation, count) in new[] { (1, 1 + Math.Max(0, extraPrefixes)), (2, 1 + Math.Max(0, extraSuffixes)) })
            for (int i = 0; i < count; i++)
            {
                var available = RelicModPool.Where(m => m.GenerationType == generation && !chosen.Any(c => c.Hash == m.Hash)
                    && !m.Families.Any(families.Contains)).ToArray();
                if (available.Length == 0) break;
                var mod = available[rng.Next(available.Length)];
                chosen.Add(mod);
                families.UnionWith(mod.Families);
            }
        return chosen;
    }

    /// <summary>The stat pairs of these mods, summed by stat: what the <c>0x74 03</c> summary carries for the covered
    /// remnants [capture: with 1 remnant covered the summary is its two mods' pairs (600 → 15, 14364 → 80), with 2 covered
    /// the two remnants' sums (30 / 160)].</summary>
    public static IReadOnlyList<(uint Stat, int Value)> SumStats(IEnumerable<RelicMod> mods)
    {
        var sums = new SortedDictionary<uint, int>();
        foreach (var mod in mods)
            foreach (var (stat, value) in mod.Stats)
                sums[stat] = sums.GetValueOrDefault(stat) + value;
        return [.. sums.Where(s => s.Value != 0).Select(s => (s.Key, s.Value))];
    }

    // [estimate] the dirt: 1.32 × the markers' count, 5% large and 15% medium.
    public const double DirtPerMarker = 1.32;
    public const double DirtLargeChance = 0.05;
    public const double DirtMediumChance = 0.15;

    // [estimate] the reveal within 250 cells of the detonator; [capture] the start within 50 (47.0 / 49.0 / 47.4).
    public const double RevealDist = 250;
    public const double StartDist = 50;
    // [capture] a placement is clamped to 90 cells from the chain's end, with a pole every 17.5 cells or less.
    public const double PlaceRange = 90;
    public const double PoleGap = 17.5;
    // [capture] a blast digs every marker within 30 cells.
    public const double BlastRadius = 30;

    // [capture] the chain, in frames at 30 fps: the first fuse burns 13 frames after the detonation, each next step 6
    // frames later; the first blast is 4 frames after its explosive lights, the others 91; a dug chest is ready 16 frames
    // after its blast.
    public const int FirstFuseFrames = 13;
    public const int StepFrames = 6;
    public const int FirstBlastFrames = 4;
    public const int BlastFrames = 91;
    public const int ChestReadyFrames = 16;
    public const double FramesPerSecond = 30;

    /// <summary>[estimate] a chest's item count: Low 1-2, Mid 2-3, High 3-4.</summary>
    public static (int Min, int Max) ChestItems(ChestTier tier) => tier switch
    {
        ChestTier.Low => (1, 2),
        ChestTier.Mid => (2, 3),
        _ => (3, 4),
    };

    /// <summary>One cell in world units (250 / 23, the client's own).</summary>
    public const double Cell = 250.0 / 23.0;

    /// <summary>The world point a fuse's Beam end carries: the cell's centre [capture: all 38 fuse creates; a fuse
    /// of the level-73 capture names (11353.3, 13212.0), which is (1044.5, 1215.5) × 250 / 23, the pole at
    /// (1044, 1215)].</summary>
    public static (float X, float Y) BeamPos((int X, int Y) cell) =>
        ((float)((cell.X + 0.5) * Cell), (float)((cell.Y + 0.5) * Cell));

    /// <summary>A <c>MonsterPacks</c> row of the league: its size range and its <c>MonsterPackEntries</c> (the
    /// MonsterVarieties row and the weight of each).</summary>
    public sealed record Pack(string Id, int MinCount, int MaxCount, IReadOnlyList<(int Variety, int Weight)> Members);

    /// <summary>The packs a regular marker raises [data: the <c>expedition-fodder1</c> to <c>5</c> rows of MonsterPacks, with
    /// their entries]. The *low and subarea rows are other content's.</summary>
    public static IReadOnlyList<Pack> RegularPacks { get; private set; } = [];

    /// <summary>The packs an elite marker raises for a faction [data: the faction's <c>expedition-&lt;faction id&gt;-elite&lt;n&gt;</c>
    /// rows of MonsterPacks, named after ExpeditionFactions.Id; the tables carry no tag that links a pack to a faction].</summary>
    public static IReadOnlyList<Pack> ElitePacks(string factionId) =>
        ElitePackRows.GetValueOrDefault(factionId) ?? [];

    /// <summary>Every MonsterVarieties row an encounter of this faction can raise, for the 0x20 monster types.</summary>
    public static IReadOnlyList<int> VarietiesOf(string factionId) =>
        [.. RegularPacks.Concat(ElitePacks(factionId)).SelectMany(p => p.Members).Select(m => m.Variety).Distinct()];

    static Dictionary<string, IReadOnlyList<Pack>> ElitePackRows = [];

    /// <summary>The WorldAreas Id of an area's HASH16 (what a new instance's request names), null for none.</summary>
    public static string? AreaId(ushort hash) => AreaIds.GetValueOrDefault(hash);

    static Dictionary<ushort, string> AreaIds = [];
    static Dictionary<int, (int, int, int)> BalanceRows = [];
    static Dictionary<string, (IReadOnlyList<string>, StateMachineData?)> ObjectTypeRows = [];
    static HashSet<string> ChestPathRows = [];

    /// <summary>Builds this module's game data (its module's LoadData).</summary>
    public static void LoadGameData()
    {
        RelicAo = ClientTables.TableOrEmpty("ExpeditionRelics").FirstOrDefault(r => r.TextOrNull("Id") == "CommonRemnant")
            is { Table: not null } relic ? relic.TextOrNull("AOFile") ?? "" : "";
        PluginId = ClientTables.TableOrEmpty("TerrainPlugins").FirstOrDefault(r => r.TextOrNull("Id") == "ExpeditionPlugin")
            is { Table: not null } plugin && plugin.IntOrNull("__col1") is int id ? (ushort)id : null;
        RulesetRow = ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.TextOrNull("Id") == "ExpeditionRules")
            is { Table: not null } ruleset && ruleset.Index <= byte.MaxValue ? (byte)ruleset.Index : null;
        var areaIds = new Dictionary<ushort, string>();
        foreach (var area in ClientTables.TableOrEmpty("WorldAreas"))
            if (area.IntOrNull("HASH16") is int hash && area.TextOrNull("Id") is { Length: > 0 } areaId) areaIds.TryAdd((ushort)hash, areaId);
        AreaIds = areaIds;
        BalanceRows = BuildBalanceRows();
        ObjectTypeRows = BuildObjectTypeRows();
        ChestPathRows = BuildChestPathRows();
        RelicModPool = BuildRelicModPool();
        var packs = BuildPacks();
        RegularPacks = [.. packs.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^expedition-fodder[1-5]$"))];
        ElitePackRows = FactionIds.ToDictionary(f => f, f => (IReadOnlyList<Pack>)[.. packs.Where(p =>
            p.Id.StartsWith($"expedition-{f.ToLowerInvariant()}-elite", StringComparison.Ordinal))]);
    }

    static Dictionary<int, (int, int, int)> BuildBalanceRows()
    {
        var rows = new Dictionary<int, (int, int, int)>();
        foreach (var r in ClientTables.TableOrEmpty("ExpeditionBalancePerLevel"))
            if (r.IntOrNull("Level") is int level && (r.IntOrNull("__col1") ?? 1) == 0)
                rows.TryAdd(level, (r.IntOrNull("__col2") ?? Chests, r.IntOrNull("__col4") ?? Explosives, r.IntOrNull("__col5") ?? Remnants));
        return rows;
    }

    static Dictionary<string, (IReadOnlyList<string>, StateMachineData?)> BuildObjectTypeRows()
    {
        var shared = ObjectTypes.SharedStates();
        var rows = new Dictionary<string, (IReadOnlyList<string>, StateMachineData?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, row) in ObjectTypes.All())
            if (type.Contains("Expedition", StringComparison.OrdinalIgnoreCase) || type == EffectType)
                rows[type] = (ObjectTypes.Components(row), ObjectTypes.StateMachine(row, shared));
        // The Chests rows of this league: each one's components are its InheritsFrom type's.
        foreach (var chest in ClientTables.TableOrEmpty("Chests"))
            if (chest.TextOrNull("Id") is { Length: > 0 } id
                && id.Contains("Expedition", StringComparison.OrdinalIgnoreCase)
                && !rows.ContainsKey(id)
                && chest.TextOrNull("InheritsFrom") is { Length: > 0 } parent
                && rows.TryGetValue(parent, out var inherited))
                rows[id] = inherited;
        return rows;
    }

    static HashSet<string> BuildChestPathRows()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("Chests"))
            if (r.TextOrNull("Id") is { Length: > 0 } id
                && id.StartsWith("Metadata/Chests/LeaguesExpedition/LeagueFaction", StringComparison.Ordinal))
                paths.Add(id);
        return paths;
    }

    static List<RelicMod> BuildRelicModPool()
    {
        var pool = new List<RelicMod>();
        foreach (var r in ClientTables.TableOrEmpty("ExpeditionRelicMods"))
        {
            if (r.IntOrNull("Mod") is not int row || Items.Mods.Row(row) is not { } mod) continue;
            var categories = r.Ints("Categories");
            var values = mod.Stats.Select(s => s.Min).ToList();
            var stats = new List<(uint, int)>();
            for (int i = 0; i < mod.Stats.Count; i++) stats.Add((mod.Stats[i].Id, values[i]));
            pool.Add(new RelicMod(mod.Id, mod.Hash, categories.Length > 0 ? categories[0] : 0, values, stats) { GenerationType = mod.GenerationType, Families = mod.Families });
        }
        return pool;
    }

    /// <summary>The league's MonsterPacks rows (their Id starts with <c>expedition-</c>) with their entries.</summary>
    static List<Pack> BuildPacks()
    {
        var entries = new Dictionary<int, List<(int, int)>>();
        foreach (var e in ClientTables.TableOrEmpty("MonsterPackEntries"))
            if (e.IntOrNull("MonsterPacksKey") is int pack && e.IntOrNull("MonsterVarietiesKey") is int variety)
            {
                if (!entries.TryGetValue(pack, out var list)) entries[pack] = list = [];
                list.Add((variety, Math.Max(1, e.IntOrNull("Weight") ?? 1)));
            }
        var packs = new List<Pack>();
        foreach (var r in ClientTables.TableOrEmpty("MonsterPacks"))
            if (r.TextOrNull("Id") is { } id && id.StartsWith("expedition-", StringComparison.Ordinal) && entries.TryGetValue(r.Index, out var members))
                packs.Add(new Pack(id, Math.Max(1, r.Int("MinCount")), Math.Max(r.Int("MinCount"), r.Int("MaxCount")), members));
        return packs;
    }
}

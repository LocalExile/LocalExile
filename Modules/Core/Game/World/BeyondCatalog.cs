using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>The client's Beyond factions (BeyondFactions), their Scourge/Hellscape monster packs and bosses,
/// portal object types, Hellscape life scaling, and Tainted currency drop pool.</summary>
public static class BeyondCatalog
{
    public const string MonsterPrefix = "Metadata/Monsters/LeagueHellscape/";
    public const string SmallPortalType = "Metadata/Monsters/BeyondDemons/BeyondPortal1";
    public const string PackPortalType = "Metadata/Monsters/BeyondDemons/BeyondPortal2";
    public const string BossPortalType = "Metadata/Monsters/BeyondDemons/BeyondPortal3";

    /// <summary>One BeyondFactions row: its boss variety, faction-weight stat, Demonic Power boss buff, and packs.</summary>
    public sealed record Faction(
        int Row,
        string Id,
        string BossVariety,
        int BossVarietyRow,
        string ChanceStat,
        ushort BossBuffRow,
        IReadOnlyList<(string Stat, int Value)> BossBuffStats,
        IReadOnlyList<PackCatalog.Pack> FodderPacks,
        IReadOnlyList<PackCatalog.Pack> ElitePacks);

    /// <summary>Resolved portal type metadata for BeyondPortal1..3.</summary>
    public sealed record PortalSpec(string Type, IReadOnlyList<string> Components, byte TransitionableIndex, StateMachineData? StateMachine);

    // Weighted 3.29 Beyond Tainted currency pool (gated by BaseItemTypes.DropLevel <= monster level).
    static readonly (string Id, int Weight)[] TaintedPool =
    [
        ("Metadata/Items/Currency/CurrencyHellscapeArmourQuality", 30),
        ("Metadata/Items/Currency/CurrencyHellscapeWeaponQuality", 30),
        ("Metadata/Items/Currency/CurrencyHellscapeRerollSocketColours", 25),
        ("Metadata/Items/Currency/CurrencyHellscapeRerollSocketNumbers", 20),
        ("Metadata/Items/Currency/CurrencyHellscapeRerollSocketLinks", 12),
        ("Metadata/Items/Currency/CurrencyHellscapeRerollRare", 10),
        ("Metadata/Items/Currency/CurrencyHellscapeAddModToRare", 5),
        ("Metadata/Items/Currency/CurrencyHellscapeUpgradeToUnique", 4),
        ("Metadata/Items/Currency/CurrencyHellscapeUpgradeModTier", 2),
    ];

    static readonly Lazy<IReadOnlyList<Faction>> Data = new(Load);
    static readonly Lazy<int[]> LifeScaling = new(LoadLifeScaling);
    static readonly Lazy<Dictionary<string, PortalSpec>> Portals = new(LoadPortals);

    public static IReadOnlyList<Faction> All => Data.Value;

    static readonly Lazy<Dictionary<int, Faction>> ByRows = new(() => All.ToDictionary(f => f.Row));
    static readonly Lazy<Dictionary<string, Faction>> ByBoss = new(() => All.ToDictionary(f => f.BossVariety, StringComparer.Ordinal));
    static readonly Lazy<HashSet<int>> BossRows = new(() => [.. All.Select(f => f.BossVarietyRow)]);

    public static Faction? ByRow(int row) => ByRows.Value.GetValueOrDefault(row);
    public static Faction? ByBossVariety(string? varietyId) => varietyId is not null ? ByBoss.Value.GetValueOrDefault(varietyId) : null;
    public static bool IsBoss(MonsterCatalog.Variety? variety) => variety is not null && BossRows.Value.Contains(variety.Row);

    /// <summary>Fast prefix check: all Beyond/Hellscape demon varieties sit under <see cref="MonsterPrefix"/>.</summary>
    public static bool IsBeyondVariety(string? varietyId) =>
        varietyId is not null && varietyId.StartsWith(MonsterPrefix, StringComparison.Ordinal);

    /// <summary>The BeyondFactions row (0 Demon, 1 Flesh, 2 Pale) of a Hellscape variety id, or -1.</summary>
    public static int FactionOfVariety(string? varietyId)
    {
        if (!IsBeyondVariety(varietyId)) return -1;
        if (varietyId!.Contains("/DemonFaction/", StringComparison.Ordinal)) return 0;
        if (varietyId.Contains("/FleshFaction/", StringComparison.Ordinal)) return 1;
        if (varietyId.Contains("/PaleFaction/", StringComparison.Ordinal)) return 2;
        return -1;
    }

    /// <summary>Portal object specification (components, Transitionable index, StateMachine) from client.db.</summary>
    public static PortalSpec? Portal(string type) => Portals.Value.GetValueOrDefault(type);

    /// <summary>Picks one faction weighted by <c>max(0, 100 + stat(ChanceStat))</c> (uniform fallback if all zero).</summary>
    public static Faction? PickFaction(Func<string, int> stat, Random random)
    {
        if (All.Count == 0) return null;
        var weights = new int[All.Count];
        int total = 0;
        for (int i = 0; i < All.Count; i++)
        {
            weights[i] = Math.Max(0, 100 + stat(All[i].ChanceStat));
            total += weights[i];
        }
        if (total <= 0) return All[random.Next(All.Count)];
        int roll = random.Next(total);
        for (int i = 0; i < All.Count; i++)
            if ((roll -= weights[i]) < 0) return All[i];
        return All[^1];
    }

    /// <summary>Picks one weighted Tainted currency item eligible for <paramref name="level"/>.</summary>
    public static string? PickTainted(ItemCatalog items, int level, Random random)
    {
        int total = 0;
        for (int i = 0; i < TaintedPool.Length; i++)
            if (items.Find(TaintedPool[i].Id) is { Supported: true } t && t.DropLevel <= level)
                total += TaintedPool[i].Weight;
        if (total <= 0) return null;
        int roll = random.Next(total);
        for (int i = 0; i < TaintedPool.Length; i++)
            if (items.Find(TaintedPool[i].Id) is { Supported: true } t && t.DropLevel <= level
                && (roll -= TaintedPool[i].Weight) < 0)
                return TaintedPool[i].Id;
        return null;
    }

    /// <summary>HellscapeLifeScalingPerLevel (AdditionalLifeScaling index 7, set by HellscapeMonsterBase.ot):
    /// +50% at level 1, +318% at 68, +378% at 83, +382% at 84+.</summary>
    public static int LifePct(int level)
    {
        var table = LifeScaling.Value;
        return table.Length == 0 ? 0 : table[Math.Clamp(level, 1, table.Length) - 1];
    }

    /// <summary>Mandatory StateMachine overrides for LeagueHellscape monsters: <c>spawned = 1</c> prevents
    /// HellscapeMonsterBase.ot's client-side <c>SetRenderHeightOffset(-400/-300/-200)</c> from burying them underground;
    /// <c>souler_coasters = 0</c> clears Beidat's intro effect attachment.</summary>
    public static StateMachineData? StartStates(MonsterCatalog.Variety variety)
    {
        if (!IsBeyondVariety(variety.Id) || variety.StateMachine is not { } sm) return null;
        sm = sm.Set("spawned", 1);
        if (variety.Id.EndsWith("/HellscapePaleBoss", StringComparison.Ordinal))
            sm = sm.Set("souler_coasters", 0);
        return sm;
    }

    static IReadOnlyList<Faction> Load()
    {
        if (!ClientTables.Has("BeyondFactions")) return [];
        var buffTemplates = ClientTables.Has("BuffTemplates") ? ClientTables.Table("BuffTemplates") : null;
        var buffDefs = ClientTables.Has("BuffDefinitions") ? ClientTables.Table("BuffDefinitions") : null;
        var result = new List<Faction>();
        foreach (var row in ClientTables.Table("BeyondFactions"))
        {
            string id = row.Text("Id") ?? "";
            int bossRow = row.Int("Boss");
            if (Game.Monsters.Find(bossRow.ToString()) is not { Supported: true } boss) continue;
            string chanceStat = row.IntOrNull("FactionChanceStat") is int statRow && statRow >= 0
                ? Program.Stats.Name((uint)statRow + 1) : "";
            ushort buffRow = 0;
            var buffStats = new List<(string Stat, int Value)>();
            if (buffTemplates is not null && buffDefs is not null
                && row.IntOrNull("BuffTemplate") is int templateRow && templateRow >= 0 && templateRow < buffTemplates.Count)
            {
                var tmpl = buffTemplates[templateRow];
                if (tmpl.IntOrNull("BuffDefinitionsKey") is int defRow && defRow >= 0 && defRow < buffDefs.Count)
                {
                    buffRow = (ushort)defRow;
                    var statKeys = buffDefs[defRow].Ints("StatsKeys");
                    var statVals = tmpl.Ints("Buff_StatValues");
                    for (int i = 0; i < statKeys.Length && i < statVals.Length; i++)
                    {
                        string name = Program.Stats.Name((uint)statKeys[i] + 1);
                        if (!string.IsNullOrEmpty(name)) buffStats.Add((name, statVals[i]));
                    }
                }
            }
            string slug = id.ToLowerInvariant();
            var fodder = new List<PackCatalog.Pack>();
            for (int i = 1; i <= 3; i++)
                if (Game.Packs.Find($"hellscape-{slug}-fodder{i}") is { } p && PackSupported(p)) fodder.Add(p);
            var elite = new List<PackCatalog.Pack>();
            for (int i = 1; i <= 2; i++)
                if (Game.Packs.Find($"hellscape-{slug}-elite{i}") is { } p && PackSupported(p)) elite.Add(p);
            result.Add(new(row.Index, id, boss.Id, boss.Row, chanceStat, buffRow, buffStats, fodder, elite));
        }
        return result;
    }

    static bool PackSupported(PackCatalog.Pack pack) =>
        pack.Entries.Any(e => Game.Monsters.Find(e.Variety.ToString()) is { Supported: true });

    static int[] LoadLifeScaling()
    {
        if (!ClientTables.Has("HellscapeLifeScalingPerLevel")) return [];
        var rows = ClientTables.Table("HellscapeLifeScalingPerLevel")
            .Select(r => (Level: r.Int("Level"), Life: r.Int("MoreLife")))
            .Where(r => r.Level >= 1).OrderBy(r => r.Level).ToArray();
        if (rows.Length == 0) return [];
        var table = new int[rows[^1].Level];
        foreach (var (level, life) in rows) table[level - 1] = life;
        return table;
    }

    static Dictionary<string, PortalSpec> LoadPortals()
    {
        var specs = new Dictionary<string, PortalSpec>(StringComparer.Ordinal);
        var shared = ObjectTypes.SharedStates();
        string[] paths = [SmallPortalType, PackPortalType, BossPortalType];
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        foreach (var (type, row) in ObjectTypes.All())
        {
            if (!wanted.Contains(type)) continue;
            var components = ObjectTypes.Components(row);
            var sm = components.Contains("StateMachine") ? ObjectTypes.StateMachine(row, shared) : null;
            int idx = QuestObject.TransitionableIndex(components);
            if (idx >= 0 && QuestObject.CanSerialize(components, sm is not null))
                specs[type] = new(type, components, (byte)idx, sm);
        }
        return specs;
    }
}

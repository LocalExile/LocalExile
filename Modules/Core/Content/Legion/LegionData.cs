using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Legion client.db tables, read once per module load (LegionFactions, LegionRanks, LegionMonsterVarieties,
/// LegionMonsterCounts, LegionChestTypes, LegionChestCounts, LegionBalancePerLevel). Columns whose meaning is not
/// known stay raw ints named by their column number.
/// </summary>
public sealed class LegionData
{
    /// <summary>A LegionFactions row: schema columns plus col3 formation orderliness (0.95 orderly grid, 0.65/0.50 loose).</summary>
    public sealed record Faction(int Row, string Id, int SpawnWeight, int BalanceKey, int StatsKey, int BuffVisualsKey, int[] MiscAnimated, double Orderliness = 0.5);

    /// <summary>A LegionRanks row; col0 is tier (0..3), col1..col4 rarity weights, col7 general/hoard chance, col8 alert value (phase 4).</summary>
    public sealed record Rank(int Row, int Col0, int Col1, int Col2, int Col3, int Col4, int BalanceKey, int Col6, int Col7, int Col8);

    /// <summary>A LegionMonsterVarieties row; col3 meaning unknown, col5 prop chance (%), col6 variety spawn weight,
    /// col10/col11 prop clip time range (ms), col15 prop count per MiscAnimatedKey entry.</summary>
    public sealed record MonsterVariety(int Row, int Monster, int Faction, int RankKey, int Col3, int[] MiscAnimated, int PropChance = 0, int SpawnWeight = 1, int[]? PropFromMs = null, int[]? PropToMs = null, int[]? PropCounts = null);

    /// <summary>A LegionMonsterCounts row; cols 2-7 meaning unknown.</summary>
    public sealed record MonsterCount(int Row, int Faction, int RankKey, int Col2, int Col3, int Col4, int Col5, int Col6, int Col7);

    /// <summary>A LegionChestTypes row; col0 is the frozen chest monster variety, Chest/HardmodeChest are the Chests rows it becomes at release, col3 meaning unknown.</summary>
    public sealed record ChestType(int Row, int Col0, int Chest, int Col3, int Faction, int HardmodeChest = -1);

    /// <summary>A LegionChestCounts row; cols 2-5 and 7 meaning unknown.</summary>
    public sealed record ChestCount(int Row, int Faction, int RankKey, int MinLevel, int Col2, int Col3, int Col4, int Col5, int Col7);

    /// <summary>A LegionBalancePerLevel row from MinLevel; Cols holds col1..col15 (col3 timer ms, col15 Timeless Conflict alert cap).</summary>
    public sealed record Balance(int Row, int MinLevel, int[] Cols)
    {
        /// <summary>col3: timer length in ms (client 0x1416290a0).</summary>
        public int DurationMs => Cols[2];
        /// <summary>col15: maximum Alert level before Schism triggers under the Timeless Conflict keystone (1000).</summary>
        public int AlertMax => Cols.Length >= 15 && Cols[14] > 0 ? Cols[14] : 1000;
    }

    /// <summary>One of the four Domain of Timeless Conflict areas (WorldAreas LegionLeague..LegionLeague4, 2..5 emblems).</summary>
    public sealed record DomainArea(int Row, ushort Hash16, string Id, string Name, int BaseLevel, int[] ModRows);

    /// <summary>A Timeless Emblem or Unrelenting Timeless Emblem (BaseItemTypes 3055..3064) and its MapFragmentMods entry.</summary>
    public sealed record EmblemInfo(int BaseRow, string BaseId, uint TypeHash, string Name, string FactionId, int Family, bool Uber, int[] ModRows);

    /// <summary>A LegionRewardTypeVisuals row: its minimap icon (MinimapIcons row; the monster stat is the row + 1),
    /// its MiscAnimatedKey (.ao floating symbol above clickable chests) and its scale (__col4). The client draws the symbol by row.</summary>
    public sealed record RewardType(int Row, int MinimapIcon, int MiscAnimatedKey = -1, double Scale = 3.0);

    public IReadOnlyList<RewardType> RewardTypes { get; }
    /// <summary>O(1) lookup of LegionRewardTypeVisuals by row index.</summary>
    public IReadOnlyDictionary<int, RewardType> RewardTypesByRow { get; }
    public IReadOnlyList<Faction> Factions { get; }
    public IReadOnlyList<Rank> Ranks { get; }
    public IReadOnlyList<MonsterVariety> MonsterVarieties { get; }
    /// <summary>O(1) lookup of first LegionMonsterVarieties row by MonsterVarietiesKey (for stasis props).</summary>
    public IReadOnlyDictionary<int, MonsterVariety> VarietyByMonster { get; }
    public IReadOnlyList<MonsterCount> MonsterCounts { get; }
    public IReadOnlyList<ChestType> ChestTypes { get; }
    public IReadOnlyList<ChestCount> ChestCounts { get; }
    public IReadOnlyList<Balance> Balances { get; }
    /// <summary>Environments rows for LegionLeagueWorld, LegionLeagueWorldBattle, LegionLeagueWorldPostBattle and LegionLeague (Domain).</summary>
    public ushort EnvWorld { get; }
    public ushort EnvBattle { get; }
    public ushort EnvPostBattle { get; }
    public ushort EnvDomain { get; }
    /// <summary>Rulesets row for LegionRules, or null when absent from client.db.</summary>
    public byte? RulesetRow { get; }
    /// <summary>MiscAnimated row -> AOFile path, loaded eagerly so hot-reload clears it cleanly.</summary>
    public IReadOnlyDictionary<int, string> MiscAnimatedModels { get; }
    /// <summary>Chests rows referenced by LegionChestTypes (row -> Id and InheritsFrom).</summary>
    public IReadOnlyDictionary<int, (string Id, string? InheritsFrom)> ChestsByRow { get; }
    /// <summary>Chest Id -> InheritsFrom for Legion chests (used when resolving QuestObjectTypes for openable chests).</summary>
    public IReadOnlyDictionary<string, string?> ChestInherits { get; }
    /// <summary>Ordered Domain of Timeless Conflict areas: [0]=2-way (LegionLeague), [1]=3-way, [2]=4-way, [3]=5-way (LegionLeague4).</summary>
    public IReadOnlyList<DomainArea> DomainAreas { get; }
    /// <summary>AtlasNode HASH16 for LegionDomain (50774), or 0 when absent.</summary>
    public ushort DomainAtlasNodeHash { get; }
    /// <summary>Timeless Emblems and Unrelenting Emblems indexed by BaseItemTypes row and TypeHash.</summary>
    public IReadOnlyDictionary<int, EmblemInfo> EmblemsByRow { get; }
    public IReadOnlyDictionary<uint, EmblemInfo> EmblemsByHash { get; }

    static LegionData? Loaded;
    public static LegionData Current => Loaded ?? throw new InvalidOperationException("LegionData not loaded; call LegionModule.LoadData() first.");
    public static LegionData Reload() => Loaded = new LegionData();
    public static void Unload() => Loaded = null;

    /// <summary>An optional column (the prop columns are a guess) that is a list or a number: its numbers, or none when it is neither, so a type surprise
    /// skips the props instead of failing the module.</summary>
    static int[] Tolerant(ClientRow r, string column)
    {
        try { return r.Ints(column); }
        catch (Exception) { }
        try { return r.IntOrNull(column) is int v ? [v] : []; }
        catch (Exception) { return []; }
    }

    LegionData()
    {
        string[] animated = ["MiscAnimatedKey1", "MiscAnimatedKey2", "MiscAnimatedKey3", "MiscAnimatedKey4", "MiscAnimatedKey5"];
        Factions = [.. ClientTables.TableOrEmpty("LegionFactions").Select(r => new Faction(r.Index, r.Text("Id") ?? "", r.Int("SpawnWeight"),
            r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.IntOrNull("StatsKey") ?? -1, r.IntOrNull("BuffVisualsKey") ?? -1,
            [.. animated.Select(c => r.IntOrNull(c) ?? -1)], r.RealOrNull("__col3") ?? 0.5))];
        Ranks = [.. ClientTables.TableOrEmpty("LegionRanks").Select(r => new Rank(r.Index, r.Int("__col0"), r.Int("__col1"), r.Int("__col2"),
            r.Int("__col3"), r.Int("__col4"), r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.Int("__col6"), r.Int("__col7"), r.Int("__col8")))];
        MonsterVarieties = [.. ClientTables.TableOrEmpty("LegionMonsterVarieties").Select(r =>
        {
            int rank = r.IntOrNull("LegionRanksKey") ?? -1;
            int weight = Tolerant(r, "__col6").FirstOrDefault();
            return new MonsterVariety(r.Index, r.IntOrNull("MonsterVarietiesKey") ?? -1, r.IntOrNull("LegionFactionsKey") ?? -1, rank,
                r.Int("__col3"), r.Ints("MiscAnimatedKey"), Tolerant(r, "__col5").FirstOrDefault(), weight > 0 ? weight : 1,
                Tolerant(r, "__col10"), Tolerant(r, "__col11"), Tolerant(r, "__col15"));
        })];
        MonsterCounts = [.. ClientTables.TableOrEmpty("LegionMonsterCounts").Select(r => new MonsterCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("__col2"), r.Int("__col3"), r.Int("__col4"),
            r.Int("__col5"), r.Int("__col6"), r.Int("__col7")))];
        ChestTypes = [.. ClientTables.TableOrEmpty("LegionChestTypes").Select(r => new ChestType(r.Index, r.IntOrNull("__col0") ?? -1,
            r.IntOrNull("Chest") ?? -1, r.Int("__col3"), r.IntOrNull("Faction") ?? -1, r.IntOrNull("HardmodeChest") ?? -1))];
        ChestCounts = [.. ClientTables.TableOrEmpty("LegionChestCounts").Select(r => new ChestCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("MinLevel"), r.Int("__col2"), r.Int("__col3"),
            r.Int("__col4"), r.Int("__col5"), r.Int("__col7")))];
        RewardTypes = [.. ClientTables.TableOrEmpty("LegionRewardTypeVisuals").Select(r => new RewardType(
            r.Index, r.IntOrNull("MinimapIconsKey") ?? -1, r.IntOrNull("MiscAnimatedKey") ?? -1, r.RealOrNull("__col4") ?? 3.0))];
        RewardTypesByRow = RewardTypes.ToDictionary(r => r.Row);
        var byMonster = new Dictionary<int, MonsterVariety>();
        foreach (var mv in MonsterVarieties)
            if (mv.Monster >= 0) byMonster.TryAdd(mv.Monster, mv);
        VarietyByMonster = byMonster;
        Balances = [.. ClientTables.TableOrEmpty("LegionBalancePerLevel").Select(r => new Balance(r.Index, r.Int("MinLevel"),
            [.. Enumerable.Range(1, 15).Select(c => r.Int($"__col{c}"))]))];
        var environments = ClientTables.TableOrEmpty("Environments");
        ushort Env(string id) => environments.FirstOrDefault(r => r.Text("Id") == id) is { Table: not null } row && row.Index <= ushort.MaxValue ? (ushort)row.Index : (ushort)0;
        EnvWorld = Env("LegionLeagueWorld");
        EnvBattle = Env("LegionLeagueWorldBattle");
        EnvPostBattle = Env("LegionLeagueWorldPostBattle");
        EnvDomain = Env("LegionLeague");
        RulesetRow = ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == "LegionRules") is { Table: not null } rr && rr.Index <= byte.MaxValue
            ? (byte)rr.Index : null;
        MiscAnimatedModels = ClientTables.TableOrEmpty("MiscAnimated")
            .Where(r => r.Text("AOFile") is { Length: > 0 })
            .ToDictionary(r => r.Index, r => r.Text("AOFile")!);
        var chestRows = new HashSet<int>();
        foreach (var ct in ChestTypes)
        {
            if (ct.Chest >= 0) chestRows.Add(ct.Chest);
            if (ct.HardmodeChest >= 0) chestRows.Add(ct.HardmodeChest);
        }
        var byRow = new Dictionary<int, (string Id, string? InheritsFrom)>();
        var inherits = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("Chests"))
        {
            if (!chestRows.Contains(r.Index) || r.Text("Id") is not { Length: > 0 } cid) continue;
            string? parent = r.Text("InheritsFrom");
            byRow[r.Index] = (cid, parent);
            inherits[cid] = parent;
        }
        ChestsByRow = byRow;
        ChestInherits = inherits;

        // Domain of Timeless Conflict WorldAreas (LegionLeague..LegionLeague4) and AtlasNode (LegionDomain).
        string[] domainIds = ["LegionLeague", "LegionLeague2", "LegionLeague3", "LegionLeague4"];
        var worldAreas = ClientTables.TableOrEmpty("WorldAreas");
        var domains = new List<DomainArea>(4);
        foreach (string areaId in domainIds)
        {
            if (worldAreas.FirstOrDefault(r => r.Text("Id") == areaId) is { Table: not null } wa)
                domains.Add(new DomainArea(wa.Index, (ushort)wa.Int("HASH16"), areaId,
                    wa.Text("Name") ?? "Domain of Timeless Conflict", wa.Int("AreaLevel"), wa.Ints("ModsKeys")));
        }
        DomainAreas = domains;
        DomainAtlasNodeHash = ClientTables.TableOrEmpty("AtlasNode").FirstOrDefault(r => r.Text("Id") == "LegionDomain") is { Table: not null } an
            ? (ushort)an.Int("HASH16") : (ushort)0;

        // Normal & Unrelenting Timeless Emblems from BaseItemTypes + MapFragmentMods.
        var fragModsByBase = new Dictionary<int, (int Family, int[] Mods)>();
        foreach (var fm in ClientTables.TableOrEmpty("MapFragmentMods"))
            if (fm.IntOrNull("BaseItemTypesKey") is int baseRow)
                fragModsByBase[baseRow] = (fm.IntOrNull("MapFragmentFamilies") ?? -1, fm.Ints("ModsKeys"));
        var emblemsByRow = new Dictionary<int, EmblemInfo>();
        var emblemsByHash = new Dictionary<uint, EmblemInfo>();
        foreach (var bt in ClientTables.TableOrEmpty("BaseItemTypes"))
        {
            if (bt.Text("Id") is not { } bid || !bid.StartsWith("Metadata/Items/MapFragments/CurrencyLegionFragment", StringComparison.Ordinal)) continue;
            string suffix = bid["Metadata/Items/MapFragments/CurrencyLegionFragment".Length..];
            bool uber = suffix.EndsWith("Uber", StringComparison.Ordinal);
            string key = uber ? suffix[..^4] : suffix;
            string? faction = key switch
            {
                "Karui" => "Karui",
                "Maraketh" => "Maraketh",
                "Eternal" => "EternalEmpire",
                "Templar" => "Templar",
                "Vaal" => "Vaal",
                _ => null,
            };
            if (faction is null) continue;
            var (family, modRows) = fragModsByBase.TryGetValue(bt.Index, out var entry) ? entry : (-1, []);
            var info = new EmblemInfo(bt.Index, bid, InstanceEntities.ObjectTypeHash(bid), bt.Text("Name") ?? bid, faction, family, uber, modRows);
            emblemsByRow[bt.Index] = info;
            emblemsByHash[info.TypeHash] = info;
        }
        EmblemsByRow = emblemsByRow;
        EmblemsByHash = emblemsByHash;
    }
}

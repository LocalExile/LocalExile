using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Town;

/// <summary>
/// Read-only 3.29.3c Kingsmarch / Settlers game data loaded from <c>client.db</c> (<c>Village*</c> and <c>Words</c> tables).
/// Falls back cleanly to verified 3.29.3c retail constants when running in minimal test environments without <c>client.db</c>.
/// </summary>
public static class VillageData
{
    public const int ResourceCount = InstanceVillage.ResourceCount;       // 21
    public const int ProductionCount = InstanceVillage.ProductionCount;   // 15
    public const int FarmPlotCount = InstanceVillage.FarmPlotCount;       // 6
    public const int ShipCount = InstanceVillage.ShipCount;               // 3
    public const int PortCount = 6;                                       // VillageShippingPorts rows 1..6
    public const int MapDeviceCount = InstanceVillage.MapDeviceCount;     // 3
    public const int ExportCount = InstanceVillage.ExportCount;           // 16
    public const int QuotasPerPort = InstanceVillage.QuotasPerPort;       // 3
    public const byte EmptyFarmPlot = InstanceVillage.EmptyFarmPlot;      // 21

    // VillageJobTypes rows (0..9)
    public const byte JobTypeIdling = 0;
    public const byte JobTypeMining = 1;
    public const byte JobTypeSmelting = 2;
    public const byte JobTypeFarming = 3;
    public const byte JobTypeDisenchanting = 4;
    public const byte JobTypeMapping = 5;
    public const byte JobTypeShipping = 6;
    public const byte JobTypeFishing = 7;
    public const byte JobTypeLostAtSea = 8;
    public const byte JobTypeLostMapping = 9;

    // VillageJobs rows (0..30)
    public const byte JobIdling = 0;
    public const byte JobMiningFirst = 1;    // 1..5 = Crimson Iron .. Verisium
    public const byte JobMiningLast = 5;
    public const byte JobSmeltingFirst = 6;  // 6..10 = Crimson Iron .. Verisium
    public const byte JobSmeltingLast = 10;
    public const byte JobFarmingFirst = 11;  // 11..16 = Plot 0..5
    public const byte JobFarmingLast = 16;
    public const byte JobDisenchanting = 17;
    public const byte JobMappingFirst = 18;  // 18..20 = Map Device 0..2
    public const byte JobMappingLast = 20;
    public const byte JobShippingFirst = 21; // 21..23 = Ship 0..2
    public const byte JobShippingLast = 23;
    public const byte JobFishing = 24;
    public const byte JobLostAtSeaFirst = 25;  // 25..27
    public const byte JobLostMappingFirst = 28; // 28..30

    // VillageResources rows (0..20)
    public const int ResPendingIron = 0;
    public const int ResPendingCopper = 1;
    public const int ResPendingAmber = 2;
    public const int ResPendingBismuth = 3;
    public const int ResPendingVerisium = 4;
    public const int ResIronOre = 5;
    public const int ResCopperOre = 6;
    public const int ResAmber = 7;
    public const int ResBismuthOre = 8;
    public const int ResVerisiumOre = 9;
    public const int ResWheat = 10;
    public const int ResCorn = 11;
    public const int ResPumpkin = 12;
    public const int ResGlobulora = 13;
    public const int ResOrbisfera = 14;
    public const int ResRefinedIron = 15;
    public const int ResRefinedCopper = 16;
    public const int ResRefinedAmber = 17;
    public const int ResRefinedBismuth = 18;
    public const int ResRefinedVerisium = 19;
    public const int ResMagicDust = 20;

    // VillageBaseStats stat rows (0-based Stats._row)
    public const int StatMaxWorkers = 20728;
    public const int StatRecruitPoolSize = 20729;
    public const int StatWorkerGenRank = 20773;
    public const int StatCanMegaReroll = 21112;
    public const int StatMaxMiners = 20713;
    public const int StatMaxMineOreRank = 20714;
    public const int StatMaxSmiths = 20715;
    public const int StatMaxSmithOreRank = 20716;
    public const int StatMaxDisenchanters = 20717;
    public const int StatMaxDisenchantRarity = 20718;
    public const int StatNumFarmPlots = 20719;
    public const int StatMaxFarmersPerPlot = 20720;
    public const int StatMaxFarmCropRank = 20721;
    public const int StatNumShips = 20722;
    public const int StatMaxCrewPerShip = 20723;
    public const int StatNumPorts = 20724;
    public const int StatNumMapDevices = 20725;
    public const int StatMaxMappersPerDevice = 20726;
    public const int StatMaxFishers = 20727;
    public const int StatFarmDiffCropBonusPct = 20735;
    public const int StatFarmAdjPlotBonusPct = 20736;
    public const int StatRecruitCostPct = 20780; // 0x512d - 1

    public sealed record JobTypeInfo(byte Row, string Id, int? MaxWorkersStat, bool IsSharedCap, int? JobSpeedStat);
    public sealed record JobInfo(byte Row, byte JobType, byte SubIndex, string Status, int? RequiredStat, int RequiredStatValue);
    public sealed record ProductionInfo(byte Row, string Id, int? InputResource, int OutputResource, uint BaseProductionTime, byte? JobRow);
    public sealed record NameWord(int Row, uint Hash32, string Text);

    public static IReadOnlyDictionary<int, int> BaseStats { get; private set; } = DefaultBaseStats();
    public static IReadOnlyList<JobTypeInfo> JobTypes { get; private set; } = DefaultJobTypes();
    public static IReadOnlyList<JobInfo> Jobs { get; private set; } = DefaultJobs();
    public static IReadOnlyList<int> SkillLevelPower { get; private set; } = [12, 18, 27, 37, 49, 63];
    public static IReadOnlyList<ProductionInfo> Productions { get; private set; } = DefaultProductions();
    public static IReadOnlyList<int[]> FarmAdjacency { get; private set; } = DefaultFarmAdjacency();
    public static IReadOnlyList<NameWord> FirstNames { get; private set; } = DefaultFirstNames();
    public static IReadOnlyList<NameWord> LastNames { get; private set; } = DefaultLastNames();
    public static IReadOnlyDictionary<uint, string> WordTextByHash { get; private set; } = BuildWordLookup(DefaultFirstNames(), DefaultLastNames());

    public static uint BaseRecruitRerollCost { get; private set; } = 2_000;
    public static uint SuperRerollMultiplier { get; private set; } = 8;
    public static uint RansomCost { get; private set; } = 50_000;
    public static uint QuotaRerollCost { get; private set; } = 15_000;
    public static uint MinShipmentValue { get; private set; } = 50_000;
    public static uint MaxShipmentValue { get; private set; } = 50_000_000;

    public static int MaxWorkers => GetBaseStat(StatMaxWorkers, 90);
    public static int RecruitPoolSize => Math.Clamp(GetBaseStat(StatRecruitPoolSize, 6), 1, 12);
    public static int MaxWorkerRank => Math.Clamp(GetBaseStat(StatWorkerGenRank, 5), 1, 5);
    public static bool CanSuperReroll => GetBaseStat(StatCanMegaReroll, 1) > 0;
    public static int FarmDiffCropBonusPct => GetBaseStat(StatFarmDiffCropBonusPct, 0);
    public static int FarmAdjPlotBonusPct => GetBaseStat(StatFarmAdjPlotBonusPct, 0);

    public static int GetBaseStat(int statRow, int fallback = 0) =>
        BaseStats.TryGetValue(statRow, out int val) ? val : fallback;

    /// <summary>Worker power from <c>VillageJobSkillLevels[rank].PlayerLevelRequired</c> (<c>0x1424c2af0</c>).</summary>
    public static int WorkerPower(byte rank)
    {
        var table = SkillLevelPower;
        if (table.Count == 0) return 12;
        int idx = Math.Clamp((int)rank, 0, table.Count - 1);
        return table[idx];
    }

    /// <summary>Recruit reroll cost (<c>0x1424c7a80</c>): <c>base * (100 + stat(0x512d)) / 100 * (isSuper ? 8 : 1)</c>.</summary>
    public static uint RerollCost(bool isSuper)
    {
        long cost = BaseRecruitRerollCost;
        int pct = GetBaseStat(StatRecruitCostPct, 0);
        cost = (cost * (100 + pct)) / 100;
        if (isSuper) cost *= SuperRerollMultiplier;
        return (uint)Math.Clamp(cost, 0, uint.MaxValue);
    }

    /// <summary>Maximum workers allowed for a <c>VillageJobs</c> row (0..30), returning both cap and whether cap is shared across the JobType.</summary>
    public static (int Cap, bool SharedAcrossJobType) JobWorkerCap(byte jobRow)
    {
        if (jobRow == JobIdling || jobRow == InstanceVillage.NoneRow)
            return (MaxWorkers, true);
        if (jobRow >= Jobs.Count) return (0, false);
        var job = Jobs[jobRow];
        if (job.JobType >= JobTypes.Count) return (0, false);
        var jt = JobTypes[job.JobType];
        if (jt.MaxWorkersStat is not int statRow) return (0, false);
        return (Math.Max(0, GetBaseStat(statRow, 0)), jt.IsSharedCap);
    }

    public static string FormatWorkerName(uint firstWordHash, uint lastWordHash)
    {
        string first = WordTextByHash.TryGetValue(firstWordHash, out var f) ? f : "Kalguur";
        string last = WordTextByHash.TryGetValue(lastWordHash, out var l) ? l : " Settler";
        return first + last;
    }

    public static void LoadGameData()
    {
        try
        {
            if (ClientTables.Has("VillageBaseStats"))
            {
                var stats = new Dictionary<int, int>();
                foreach (var r in ClientTables.Table("VillageBaseStats"))
                    if (r.IntOrNull("Stat") is int stat && r.IntOrNull("StatValue") is int val)
                        stats[stat] = val;
                if (stats.Count > 0) BaseStats = stats;
            }

            if (ClientTables.Has("VillageJobTypes"))
            {
                var list = new List<JobTypeInfo>();
                foreach (var r in ClientTables.Table("VillageJobTypes"))
                {
                    list.Add(new JobTypeInfo(
                        (byte)r.Index,
                        r.TextOrNull("Id") ?? "",
                        r.IntOrNull("MaxWorkersStat"),
                        (r.IntOrNull("__col4") ?? 0) != 0,
                        r.IntOrNull("JobSpeedStat")));
                }
                if (list.Count > 0) JobTypes = list;
            }

            if (ClientTables.Has("VillageJobs"))
            {
                var list = new List<JobInfo>();
                foreach (var r in ClientTables.Table("VillageJobs"))
                {
                    list.Add(new JobInfo(
                        (byte)r.Index,
                        (byte)(r.IntOrNull("JobType") ?? 0),
                        (byte)(r.IntOrNull("__col1") ?? 0),
                        r.TextOrNull("Status") ?? "",
                        r.IntOrNull("Stat"),
                        r.IntOrNull("StatValue") ?? 0));
                }
                if (list.Count > 0) Jobs = list;
            }

            if (ClientTables.Has("VillageJobSkillLevels"))
            {
                var levels = ClientTables.Table("VillageJobSkillLevels")
                    .OrderBy(r => r.Index)
                    .Select(r => r.IntOrNull("PlayerLevelRequired") ?? 12)
                    .ToList();
                if (levels.Count > 0) SkillLevelPower = levels;
            }

            if (ClientTables.Has("VillageProduction"))
            {
                var list = new List<ProductionInfo>();
                foreach (var r in ClientTables.Table("VillageProduction"))
                {
                    list.Add(new ProductionInfo(
                        (byte)r.Index,
                        r.TextOrNull("Id") ?? "",
                        r.IntOrNull("Input"),
                        r.IntOrNull("Output") ?? 0,
                        (uint)Math.Max(1, r.IntOrNull("BaseProductionTime") ?? 120),
                        r.IntOrNull("Job") is int j ? (byte)j : null));
                }
                if (list.Count > 0) Productions = list;
            }

            if (ClientTables.Has("VillageFarmAdjacency"))
            {
                var adj = new int[FarmPlotCount][];
                for (int i = 0; i < FarmPlotCount; i++) adj[i] = [];
                foreach (var r in ClientTables.Table("VillageFarmAdjacency"))
                {
                    int plot = r.IntOrNull("Plot") ?? r.Index;
                    if (plot is < 0 or >= FarmPlotCount) continue;
                    adj[plot] = [.. r.Ints("AdjacentPlots").Where(n => n is >= 0 and < FarmPlotCount)];
                }
                FarmAdjacency = adj;
            }

            if (ClientTables.Has("VillageRecruitBalancePerTier"))
            {
                var table = ClientTables.Table("VillageRecruitBalancePerTier");
                if (table.Count > 0 && table[0].IntOrNull("__col1") is int c && c > 0)
                    BaseRecruitRerollCost = (uint)c;
            }

            if (ClientTables.Has("VillageSharedConstants"))
            {
                foreach (var r in ClientTables.Table("VillageSharedConstants"))
                {
                    string id = r.TextOrNull("Id") ?? "";
                    int val = r.IntOrNull("Value") ?? 0;
                    switch (id)
                    {
                        case "RansomCost" when val > 0: RansomCost = (uint)val; break;
                        case "MinShipmentValue" when val > 0: MinShipmentValue = (uint)val; break;
                        case "MaxShipmentValue" when val > 0: MaxShipmentValue = (uint)val; break;
                        case "RecruitSuperRerollCostMultiplier" when val > 0: SuperRerollMultiplier = (uint)val; break;
                        case "QuotaRerollCost" when val > 0: QuotaRerollCost = (uint)val; break;
                    }
                }
            }

            if (ClientTables.Has("Words"))
            {
                var first = new List<NameWord>();
                var last = new List<NameWord>();
                foreach (var r in ClientTables.Table("Words"))
                {
                    int wl = r.IntOrNull("Wordlist") ?? 0;
                    if (wl != 11 && wl != 12) continue;
                    uint h = r.UInt("HASH32");
                    string text = r.TextOrNull("Text2") ?? r.TextOrNull("Text") ?? "";
                    if (h == 0 || string.IsNullOrEmpty(text)) continue;
                    if (wl == 11) first.Add(new NameWord(r.Index, h, text));
                    else last.Add(new NameWord(r.Index, h, text));
                }
                if (first.Count > 0 && last.Count > 0)
                {
                    FirstNames = first;
                    LastNames = last;
                    WordTextByHash = BuildWordLookup(first, last);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine($"[settlers] VillageData fallback defaults used: {ex.Message}");
        }
    }

    static Dictionary<uint, string> BuildWordLookup(IReadOnlyList<NameWord> first, IReadOnlyList<NameWord> last)
    {
        var map = new Dictionary<uint, string>(first.Count + last.Count);
        foreach (var w in first) map[w.Hash32] = w.Text;
        foreach (var w in last) map[w.Hash32] = w.Text;
        return map;
    }

    static Dictionary<int, int> DefaultBaseStats() => new()
    {
        [StatMaxWorkers] = 90,
        [StatRecruitPoolSize] = 6,
        [StatWorkerGenRank] = 5,
        [StatCanMegaReroll] = 1,
        [StatMaxMineOreRank] = 5,
        [StatMaxMiners] = 12,
        [StatMaxSmithOreRank] = 5,
        [StatMaxSmiths] = 6,
        [StatNumFarmPlots] = 6,
        [StatMaxFarmersPerPlot] = 3,
        [StatMaxFarmCropRank] = 5,
        [StatFarmDiffCropBonusPct] = 0,
        [StatFarmAdjPlotBonusPct] = 0,
        [StatNumShips] = 3,
        [StatMaxCrewPerShip] = 6,
        [StatNumPorts] = 6,
        [StatMaxDisenchanters] = 6,
        [StatMaxDisenchantRarity] = 3,
        [StatNumMapDevices] = 3,
        [StatMaxMappersPerDevice] = 6,
        [StatMaxFishers] = 69,
    };

    static List<JobTypeInfo> DefaultJobTypes() =>
    [
        new(0, "Idling", StatMaxWorkers, false, null),
        new(1, "Mining", StatMaxMiners, true, 20695),
        new(2, "Smelting", StatMaxSmiths, true, 20696),
        new(3, "Farming", StatMaxFarmersPerPlot, false, 20698),
        new(4, "Disenchanting", StatMaxDisenchanters, true, 20697),
        new(5, "Mapping", StatMaxMappersPerDevice, false, 20699),
        new(6, "Shipping", StatMaxCrewPerShip, false, null),
        new(7, "Fishing", StatMaxFishers, true, 20701),
        new(8, "LostAtSea", null, false, null),
        new(9, "LostMapping", null, false, null),
    ];

    static List<JobInfo> DefaultJobs()
    {
        var list = new List<JobInfo>(31) { new(0, 0, 0, "Idling", null, 0) };
        for (byte i = 0; i < 5; i++) list.Add(new((byte)(1 + i), 1, i, "Mining", StatMaxMineOreRank, i + 1));
        for (byte i = 0; i < 5; i++) list.Add(new((byte)(6 + i), 2, i, "Smelting", StatMaxSmithOreRank, i + 1));
        for (byte i = 0; i < 6; i++) list.Add(new((byte)(11 + i), 3, i, "", StatNumFarmPlots, i + 1));
        list.Add(new(17, 4, 0, "Disenchanting", null, 0));
        for (byte i = 0; i < 3; i++) list.Add(new((byte)(18 + i), 5, i, "Mapping", StatNumMapDevices, i + 1));
        for (byte i = 0; i < 3; i++) list.Add(new((byte)(21 + i), 6, i, "", StatNumShips, i + 1));
        list.Add(new(24, 7, 0, "Fishing", null, 0));
        for (byte i = 0; i < 3; i++) list.Add(new((byte)(25 + i), 8, i, "Missing", null, 0));
        for (byte i = 0; i < 3; i++) list.Add(new((byte)(28 + i), 9, i, "Slain in the Atlas", null, 0));
        return list;
    }

    static List<ProductionInfo> DefaultProductions() =>
    [
        new(0, "Mining5", ResPendingVerisium, ResVerisiumOre, 480, 5),
        new(1, "Mining4", ResPendingBismuth, ResBismuthOre, 280, 4),
        new(2, "Mining3", ResPendingAmber, ResAmber, 180, 3),
        new(3, "Mining2", ResPendingCopper, ResCopperOre, 145, 2),
        new(4, "Mining1", ResPendingIron, ResIronOre, 120, 1),
        new(5, "Smelting5", ResVerisiumOre, ResRefinedVerisium, 480, 10),
        new(6, "Smelting4", ResBismuthOre, ResRefinedBismuth, 280, 9),
        new(7, "Smelting3", ResAmber, ResRefinedAmber, 180, 8),
        new(8, "Smelting2", ResCopperOre, ResRefinedCopper, 145, 7),
        new(9, "Smelting1", ResIronOre, ResRefinedIron, 120, 6),
        new(10, "Farming1", null, ResWheat, 360, null),
        new(11, "Farming2", null, ResCorn, 450, null),
        new(12, "Farming3", null, ResPumpkin, 540, null),
        new(13, "Farming4", null, ResGlobulora, 630, null),
        new(14, "Farming5", null, ResOrbisfera, 720, null),
    ];

    static int[][] DefaultFarmAdjacency() =>
    [
        [1, 2],
        [0, 3],
        [0, 3, 4],
        [1, 2, 5],
        [2, 5],
        [3, 4],
    ];

    static List<NameWord> DefaultFirstNames() =>
    [
        new(2891, 0x01000001u, "Are"),
        new(2892, 0x01000002u, "Jon"),
        new(2893, 0x01000003u, "Petter"),
        new(2894, 0x01000004u, "Idar"),
        new(2895, 0x01000005u, "Ingjerd"),
        new(2896, 0x01000006u, "Astrid"),
    ];

    static List<NameWord> DefaultLastNames() =>
    [
        new(2906, 0x02000001u, " Bryne"),
        new(2907, 0x02000002u, " Groven"),
        new(2908, 0x02000003u, " Sunde"),
        new(2909, 0x02000004u, " Boler"),
        new(2910, 0x02000005u, " Sorby"),
        new(2911, 0x02000006u, " Vik"),
    ];
}

/// <summary>Normalized case-insensitive league+account scope key matching SQLite <c>COLLATE NOCASE</c>.</summary>
public readonly record struct TownScope(string Account, string League)
{
    public static TownScope Of(string account, string league) =>
        new(account.Trim().ToLowerInvariant(), league.Trim().ToLowerInvariant());
}

/// <summary>One hired Kingsmarch townsfolk worker in the authoritative state.</summary>
public sealed class TownWorker
{
    public long WorkerId { get; set; }
    public uint FirstNameWordKey { get; set; }
    public uint LastNameWordKey { get; set; }
    public string Name { get; set; } = "";
    public ushort WagePerHour { get; set; }
    /// <summary><c>VillageJobs</c> row (0..30; 0 = Idling, 25..27 = LostAtSea, 28..30 = LostMapping).</summary>
    public byte AssignedJobRow { get; set; }
    public int Status { get; set; }
    public List<InstanceVillage.WorkerSkill> Skills { get; set; } = [];

    public byte RankFor(byte jobType)
    {
        for (int i = 0; i < Skills.Count; i++)
            if (Skills[i].JobTypeRow == jobType) return Skills[i].Rank;
        return 0;
    }

    public InstanceVillage.WorkerCore ToWireCore() =>
        new(FirstNameWordKey, LastNameWordKey, WagePerHour, [.. Skills]);

    public InstanceVillage.HiredWorker ToWireHired() =>
        new(ToWireCore(), AssignedJobRow);
}

/// <summary>One candidate in Raulf's recruitment pool.</summary>
public sealed class TownRecruit
{
    public uint FirstNameWordKey { get; set; }
    public uint LastNameWordKey { get; set; }
    public string Name { get; set; } = "";
    public uint HireCost { get; set; }
    public ushort WagePerHour { get; set; }
    public List<InstanceVillage.WorkerSkill> Skills { get; set; } = [];

    public InstanceVillage.WorkerCore ToWireCore() =>
        new(FirstNameWordKey, LastNameWordKey, WagePerHour, [.. Skills]);

    public InstanceVillage.RecruitEntry ToWireEntry() =>
        new(ToWireCore(), HireCost);
}

/// <summary>One of the 3 Kingsmarch ships (<c>ship_index = 0..2</c>).</summary>
public sealed class TownShip
{
    public int ShipIndex { get; set; }
    /// <summary>0 = Idle, 1 = Sailing, 2 = ReturnedWithRewards, 3 = Commandeered (Valerius ambush).</summary>
    public int State { get; set; }
    /// <summary><c>VillageShippingPorts</c> row (1..6), or <c>0xFF</c> when idle/unassigned.</summary>
    public byte PortRow { get; set; } = InstanceVillage.NoneRow;
    /// <summary><c>VillageShippingFailures</c> row (0..20), or <c>0xFF</c> when none.</summary>
    public byte FailureRow { get; set; } = InstanceVillage.NoneRow;
    public long DepartedUtc { get; set; }
    public long ArrivalUtc { get; set; }
    public uint TimerElapsed { get; set; }
    public uint TimerTotal { get; set; }
    public int RiskPercent { get; set; }
    public long ShipmentValue { get; set; }
    public uint[] CargoExports { get; set; } = new uint[VillageData.ExportCount];
    public long RansomGold { get; set; }
    public bool BossPending { get; set; }
    public bool IsAtSea => State == 1 && TimerTotal > 0 && TimerElapsed < TimerTotal && !BossPending;
}

/// <summary>One of the 3 Atlas Runner map devices (<c>device_index = 0..2</c>).</summary>
public sealed class TownMapDevice
{
    public int DeviceIndex { get; set; }
    /// <summary>0 = Idle, 1 = Running, 2 = Paused/Error/Failed, 3 = Kidnapped (Bandit Lord ambush).</summary>
    public int State { get; set; }
    public int ActiveMapSlot { get; set; } = -1;
    public long MapStartedUtc { get; set; }
    public long MapDurationSeconds { get; set; }
    public uint TimerElapsed { get; set; }
    public uint TimerTotal { get; set; }
    public byte StateFlags1 { get; set; }
    public byte StateFlags2 { get; set; }
    public List<byte> MapCompletionPercents { get; set; } = [];
    public int PortalsRemaining { get; set; }
    public bool BossAmbushPending { get; set; }
    public long RansomGold { get; set; }
    public string CurrentMapName { get; set; } = "";
    public string SummaryJson { get; set; } = "{}";
    public bool IsRunning => State == 1 && !BossAmbushPending;

    public InstanceVillage.MapDeviceState ToWire() =>
        new(TimerElapsed, TimerTotal, StateFlags1, StateFlags2, [.. MapCompletionPercents]);
}

/// <summary>One port quota entry for one of the 6 destination ports (3 slots per port).</summary>
public sealed class TownPortQuota
{
    public byte ExportRow { get; set; }
    public uint RequiredAmount { get; set; }
    public uint FulfilledAmount { get; set; }
    public int BonusPercent { get; set; }

    public InstanceVillage.PortQuotaSlot ToWire() =>
        new(ExportRow, RequiredAmount, FulfilledAmount);
}

/// <summary>
/// Tutorial / hideout invite bitflags stored in <c>village_towns.tutorial_flags</c>.
/// </summary>
[Flags]
public enum VillageTutorialFlags : long
{
    None = 0,
    JohanInvitedToHideout = 1L << 0,
    FaustusInvitedToHideout = 1L << 1,
    JohanIntroDone = 1L << 2,
    KingsmarchVisited = 1L << 3,
    FirstRecruitHired = 1L << 4,
    FirstGoldDeposited = 1L << 5,
    FirstWorkerAssigned = 1L << 6,
}

/// <summary>
/// Authoritative in-memory state for one <c>(Account, League)</c> Kingsmarch settlement, backed by <see cref="VillageStore"/>.
/// Always acquire <see cref="Gate"/> before reading or mutating any fields.
/// </summary>
public sealed class VillageTownState
{
    public Lock Gate { get; } = new();

    public string Account { get; }
    public string League { get; }
    public TownScope Scope { get; }

    public uint TreasuryGold { get; set; }
    /// <summary>Gold burn remainder in gold-seconds (<c>0..3599</c>), matching <c>0x1424c574a</c>.</summary>
    public uint GoldBurnRemainder { get; set; }
    public long LastSimulatedUtc { get; set; }
    public long LastRerollUtc { get; set; }
    public long RerollCostGold { get; set; } = 2_000;
    public long SuperRerollCostGold { get; set; } = 16_000;
    public long ShippingSeed { get; set; }
    public long NextWorkerId { get; set; } = 1;
    public long NextShipmentId { get; set; } = 1;
    public long NextMapRunId { get; set; } = 1;
    public VillageTutorialFlags TutorialFlags { get; set; }

    public uint[] Resources { get; } = new uint[VillageData.ResourceCount];
    public uint[] ProductionProgress { get; } = new uint[VillageData.ProductionCount];
    public byte[] FarmPlotCropResource { get; } = [.. Enumerable.Repeat(VillageData.EmptyFarmPlot, VillageData.FarmPlotCount)];

    public List<TownWorker> Workers { get; } = [];
    public List<TownRecruit> Recruits { get; } = [];
    public byte RecruitPoolFlag { get; set; }

    public TownShip[] Ships { get; } = [new() { ShipIndex = 0 }, new() { ShipIndex = 1 }, new() { ShipIndex = 2 }];
    public TownPortQuota[][] PortQuotas { get; } = CreateEmptyQuotas();
    public TownMapDevice[] MapDevices { get; } = [new() { DeviceIndex = 0 }, new() { DeviceIndex = 1 }, new() { DeviceIndex = 2 }];

    public uint DisenchantElapsed { get; set; }
    public uint DisenchantTotal { get; set; }
    public List<VillageStore.RewardItemRow> RewardItems { get; } = [];
    public List<VillageStore.DisenchantQueueRow> DisenchantQueue { get; } = [];

    public byte NibbleLo { get; set; }
    public byte NibbleHi { get; set; }
    public byte ActiveRewardNotice { get; set; }
    public byte ShipKidnapSlot1Based { get; set; }
    public byte ShipKidnapFlag { get; set; }
    public byte MapKidnapSlot1Based { get; set; }
    public byte MapKidnapFlag { get; set; }

    public VillageTownState(string account, string league)
    {
        Account = account;
        League = league;
        Scope = TownScope.Of(account, league);
    }

    static TownPortQuota[][] CreateEmptyQuotas()
    {
        var arr = new TownPortQuota[VillageData.PortCount][];
        for (int p = 0; p < VillageData.PortCount; p++)
        {
            arr[p] = new TownPortQuota[VillageData.QuotasPerPort];
            for (int q = 0; q < VillageData.QuotasPerPort; q++)
                arr[p][q] = new TownPortQuota();
        }
        return arr;
    }

    public InstanceVillage.VillageStateSnapshot ToWireSnapshot(ulong serverNowSeconds)
    {
        var shipTimers = new InstanceVillage.TimerState[VillageData.ShipCount];
        byte shipKidnapSlot = ShipKidnapSlot1Based;
        byte shipKidnapFlag = ShipKidnapFlag;
        for (int i = 0; i < VillageData.ShipCount; i++)
        {
            var s = Ships[i];
            if (s.State == 1 && s.ArrivalUtc > s.DepartedUtc)
            {
                long totalSec = Math.Max(1, s.ArrivalUtc - s.DepartedUtc);
                long elapsedSec = Math.Clamp((long)serverNowSeconds - s.DepartedUtc, 0, totalSec);
                uint target = Operations.VillageShippingService.ComputeTravelTime(s.PortRow, 0, 1.0).ProgressTarget;
                uint elapsedProg = (uint)Math.Clamp((long)Math.Round(target * (elapsedSec / (double)totalSec)), 0, target);
                shipTimers[i] = new(elapsedProg, target);
            }
            else if (s.State == 3)
            {
                uint target = Operations.VillageShippingService.ComputeTravelTime(s.PortRow, 0, 1.0).ProgressTarget;
                shipTimers[i] = new(target, target);
            }
            else
            {
                shipTimers[i] = new(s.TimerElapsed, s.TimerTotal);
            }
            if (shipKidnapSlot == 0 && (s.BossPending || s.State == 3))
            {
                shipKidnapSlot = (byte)(i + 1);
                shipKidnapFlag = 1;
            }
        }

        var mapStates = new InstanceVillage.MapDeviceState[VillageData.MapDeviceCount];
        byte mapKidnapSlot = MapKidnapSlot1Based;
        byte mapKidnapFlag = MapKidnapFlag;
        for (int i = 0; i < VillageData.MapDeviceCount; i++)
        {
            var d = MapDevices[i];
            mapStates[i] = d.ToWire();
            if (mapKidnapSlot == 0 && (d.BossAmbushPending || d.State == 3))
            {
                mapKidnapSlot = (byte)(i + 1);
                mapKidnapFlag = 1;
            }
        }

        uint disElapsed = DisenchantElapsed;
        uint disTotal = DisenchantTotal;
        if (DisenchantQueue.Count > 0)
        {
            var first = DisenchantQueue[0];
            disTotal = (uint)Math.Max(1, Math.Round(first.DurationSeconds * 100.0));
            disElapsed = (uint)Math.Clamp(Math.Round(first.ElapsedSeconds * 100.0), 0, disTotal);
        }

        ulong stateTs = LastSimulatedUtc > 0 ? (ulong)LastSimulatedUtc : serverNowSeconds;
        return new InstanceVillage.VillageStateSnapshot(
            Resources: [.. Resources],
            ProductionProgress: [.. ProductionProgress],
            GoldInTreasury: TreasuryGold,
            GoldBurnRemainderOrTicks: GoldBurnRemainder,
            FarmPlotCropResource: [.. FarmPlotCropResource],
            ShipTimers: shipTimers,
            MapDevices: mapStates,
            DisenchantTimer: new(disElapsed, disTotal),
            NibbleLo: NibbleLo,
            NibbleHi: NibbleHi,
            StateTimestamp: stateTs,
            ShipKidnapSlot1Based: shipKidnapSlot,
            ShipKidnapFlag: shipKidnapFlag,
            MapKidnapSlot1Based: mapKidnapSlot,
            MapKidnapFlag: mapKidnapFlag,
            ServerNowSeconds: serverNowSeconds);
    }

    public List<InstanceVillage.HiredWorker> ToWireWorkers() => [.. Workers.Select(w => w.ToWireHired())];
    public List<InstanceVillage.RecruitEntry> ToWireRecruits() => [.. Recruits.Select(r => r.ToWireEntry())];

    // ---- Persistence serialization to/from VillageStore.TownSnapshot ------------------------------------------------

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed record WorkerSkillsBlob(uint FirstWord, uint LastWord, List<SkillBlob> Skills);
    sealed record SkillBlob(byte J, byte R);
    sealed record ShipCargoBlob(uint[] Exports, uint Elapsed, uint Total);
    sealed record MapSummaryBlob(uint Elapsed, uint Total, byte F1, byte F2, List<byte> Percents, string CurrentMapName, string? Extra);
    sealed record TownExtraState(
        uint GoldBurnRemainder,
        uint[] ProductionProgress,
        byte RecruitPoolFlag,
        uint DisenchantElapsed,
        uint DisenchantTotal,
        byte NibbleLo,
        byte NibbleHi,
        byte ActiveRewardNotice,
        byte ShipKidnapSlot1Based,
        byte ShipKidnapFlag,
        byte MapKidnapSlot1Based,
        byte MapKidnapFlag);

    public VillageStore.TownSnapshot ToStoreSnapshot()
    {
        var extra = new TownExtraState(
            GoldBurnRemainder,
            [.. ProductionProgress],
            RecruitPoolFlag,
            DisenchantElapsed,
            DisenchantTotal,
            NibbleLo,
            NibbleHi,
            ActiveRewardNotice,
            ShipKidnapSlot1Based,
            ShipKidnapFlag,
            MapKidnapSlot1Based,
            MapKidnapFlag);

        var townRow = new VillageStore.TownRow(
            Account,
            League,
            TreasuryGold,
            LastSimulatedUtc,
            LastRerollUtc,
            RerollCostGold,
            SuperRerollCostGold,
            ShippingSeed,
            NextWorkerId,
            NextShipmentId,
            NextMapRunId,
            (long)TutorialFlags,
            JsonSerializer.Serialize(extra, JsonOpts));

        var resRows = new List<VillageStore.ResourceRow>(VillageData.ResourceCount);
        for (int i = 0; i < VillageData.ResourceCount; i++)
            resRows.Add(new(i, Resources[i], 0.0));

        var plotRows = new List<VillageStore.FarmPlotRow>(VillageData.FarmPlotCount);
        for (int i = 0; i < VillageData.FarmPlotCount; i++)
            plotRows.Add(new(i, FarmPlotCropResource[i], 0.0));

        var workerRows = new List<VillageStore.WorkerRow>(Workers.Count);
        for (int i = 0; i < Workers.Count; i++)
        {
            var w = Workers[i];
            var blob = new WorkerSkillsBlob(w.FirstNameWordKey, w.LastNameWordKey,
                [.. w.Skills.Select(s => new SkillBlob(s.JobTypeRow, s.Rank))]);
            workerRows.Add(new(w.WorkerId, w.Name, 0, w.WagePerHour, w.AssignedJobRow, i, w.Status,
                JsonSerializer.Serialize(blob, JsonOpts)));
        }

        var recruitRows = new List<VillageStore.RecruitRow>(Recruits.Count);
        for (int i = 0; i < Recruits.Count; i++)
        {
            var r = Recruits[i];
            var blob = new WorkerSkillsBlob(r.FirstNameWordKey, r.LastNameWordKey,
                [.. r.Skills.Select(s => new SkillBlob(s.JobTypeRow, s.Rank))]);
            recruitRows.Add(new(i, r.Name, 0, r.HireCost, r.WagePerHour,
                JsonSerializer.Serialize(blob, JsonOpts)));
        }

        var shipRows = new List<VillageStore.ShipRow>(VillageData.ShipCount);
        for (int i = 0; i < VillageData.ShipCount; i++)
        {
            var s = Ships[i];
            var cargo = new ShipCargoBlob([.. s.CargoExports], s.TimerElapsed, s.TimerTotal);
            shipRows.Add(new(
                s.ShipIndex, s.State, s.PortRow, s.DepartedUtc, s.ArrivalUtc,
                s.RiskPercent, s.ShipmentValue,
                JsonSerializer.Serialize(cargo, JsonOpts),
                "[]",
                s.FailureRow, s.RansomGold, s.BossPending));
        }

        var quotaRows = new List<VillageStore.PortQuotaRow>(VillageData.PortCount * VillageData.QuotasPerPort);
        for (int p = 0; p < VillageData.PortCount; p++)
        {
            for (int q = 0; q < VillageData.QuotasPerPort; q++)
            {
                var slot = PortQuotas[p][q];
                quotaRows.Add(new(p + 1, q, slot.ExportRow, slot.RequiredAmount, slot.FulfilledAmount, slot.BonusPercent));
            }
        }

        var devRows = new List<VillageStore.MapDeviceRow>(VillageData.MapDeviceCount);
        for (int i = 0; i < VillageData.MapDeviceCount; i++)
        {
            var d = MapDevices[i];
            var sum = new MapSummaryBlob(d.TimerElapsed, d.TimerTotal, d.StateFlags1, d.StateFlags2,
                [.. d.MapCompletionPercents], d.CurrentMapName, d.SummaryJson);
            devRows.Add(new(
                d.DeviceIndex, d.State, d.ActiveMapSlot, d.MapStartedUtc, d.MapDurationSeconds,
                d.MapCompletionPercents.Count > 0 ? d.MapCompletionPercents[^1] : 0,
                d.PortalsRemaining, d.BossAmbushPending, d.RansomGold,
                "[]", JsonSerializer.Serialize(sum, JsonOpts)));
        }

        return new VillageStore.TownSnapshot(
            townRow, resRows, plotRows, workerRows, recruitRows, shipRows, quotaRows, devRows,
            [.. RewardItems], [.. DisenchantQueue]);
    }

    public static VillageTownState FromStoreSnapshot(VillageStore.TownSnapshot snap)
    {
        var t = snap.Town;
        var state = new VillageTownState(t.Account, t.League)
        {
            TreasuryGold = (uint)Math.Clamp(t.TreasuryGold, 0, uint.MaxValue),
            LastSimulatedUtc = t.LastSimulatedUtc,
            LastRerollUtc = t.LastRerollUtc,
            RerollCostGold = t.RerollCostGold > 0 ? t.RerollCostGold : VillageData.BaseRecruitRerollCost,
            SuperRerollCostGold = t.SuperRerollCostGold > 0 ? t.SuperRerollCostGold : VillageData.RerollCost(true),
            ShippingSeed = t.ShippingSeed,
            NextWorkerId = Math.Max(1, t.NextWorkerId),
            NextShipmentId = Math.Max(1, t.NextShipmentId),
            NextMapRunId = Math.Max(1, t.NextMapRunId),
            TutorialFlags = (VillageTutorialFlags)t.TutorialFlags,
        };

        if (!string.IsNullOrWhiteSpace(t.StateJson) && t.StateJson != "{}")
        {
            try
            {
                var extra = JsonSerializer.Deserialize<TownExtraState>(t.StateJson, JsonOpts);
                if (extra is not null)
                {
                    state.GoldBurnRemainder = extra.GoldBurnRemainder % 3600u;
                    if (extra.ProductionProgress is { Length: > 0 })
                    {
                        int n = Math.Min(extra.ProductionProgress.Length, state.ProductionProgress.Length);
                        Array.Copy(extra.ProductionProgress, state.ProductionProgress, n);
                    }
                    state.RecruitPoolFlag = extra.RecruitPoolFlag;
                    state.DisenchantElapsed = extra.DisenchantElapsed;
                    state.DisenchantTotal = extra.DisenchantTotal;
                    state.NibbleLo = extra.NibbleLo;
                    state.NibbleHi = extra.NibbleHi;
                    state.ActiveRewardNotice = extra.ActiveRewardNotice;
                    state.ShipKidnapSlot1Based = extra.ShipKidnapSlot1Based;
                    state.ShipKidnapFlag = extra.ShipKidnapFlag;
                    state.MapKidnapSlot1Based = extra.MapKidnapSlot1Based;
                    state.MapKidnapFlag = extra.MapKidnapFlag;
                }
            }
            catch (JsonException) { }
        }

        foreach (var r in snap.Resources)
            if (r.ResourceId is >= 0 and < VillageData.ResourceCount)
                state.Resources[r.ResourceId] = (uint)Math.Clamp(r.Amount, 0, uint.MaxValue);

        foreach (var p in snap.FarmPlots)
            if (p.PlotIndex is >= 0 and < VillageData.FarmPlotCount)
                state.FarmPlotCropResource[p.PlotIndex] = p.CropResourceId is >= VillageData.ResWheat and <= VillageData.ResOrbisfera
                    ? (byte)p.CropResourceId
                    : VillageData.EmptyFarmPlot;

        foreach (var w in snap.Workers.OrderBy(x => x.AssignedSlot >= 0 ? x.AssignedSlot : int.MaxValue).ThenBy(x => x.WorkerId))
        {
            var (firstKey, lastKey, skills) = ParseSkillsBlob(w.SkillsJson);
            state.Workers.Add(new TownWorker
            {
                WorkerId = w.WorkerId,
                FirstNameWordKey = firstKey,
                LastNameWordKey = lastKey,
                Name = string.IsNullOrEmpty(w.Name) ? VillageData.FormatWorkerName(firstKey, lastKey) : w.Name,
                WagePerHour = (ushort)Math.Clamp(w.WagePerHour, 0, ushort.MaxValue),
                AssignedJobRow = w.AssignedJob is >= 0 and <= 30 ? (byte)w.AssignedJob : VillageData.JobIdling,
                Status = w.Status,
                Skills = skills,
            });
        }

        foreach (var r in snap.Recruits.OrderBy(x => x.SlotIndex))
        {
            var (firstKey, lastKey, skills) = ParseSkillsBlob(r.SkillsJson);
            state.Recruits.Add(new TownRecruit
            {
                FirstNameWordKey = firstKey,
                LastNameWordKey = lastKey,
                Name = string.IsNullOrEmpty(r.Name) ? VillageData.FormatWorkerName(firstKey, lastKey) : r.Name,
                HireCost = (uint)Math.Clamp(r.HireCostGold, 0, uint.MaxValue),
                WagePerHour = (ushort)Math.Clamp(r.WagePerHour, 0, ushort.MaxValue),
                Skills = skills,
            });
        }

        foreach (var s in snap.Ships)
        {
            if (s.ShipIndex is < 0 or >= VillageData.ShipCount) continue;
            var ship = state.Ships[s.ShipIndex];
            ship.State = s.State;
            ship.PortRow = s.PortId is >= 0 and <= 6 ? (byte)s.PortId : InstanceVillage.NoneRow;
            ship.FailureRow = s.EventId is >= 0 and <= 20 ? (byte)s.EventId : InstanceVillage.NoneRow;
            ship.DepartedUtc = s.DepartedUtc;
            ship.ArrivalUtc = s.ArrivalUtc;
            ship.RiskPercent = s.RiskPercent;
            ship.ShipmentValue = s.ShipmentValue;
            ship.RansomGold = s.RansomGold;
            ship.BossPending = s.BossPending;
            try
            {
                var cargo = JsonSerializer.Deserialize<ShipCargoBlob>(s.CargoJson, JsonOpts);
                if (cargo is not null)
                {
                    if (cargo.Exports is { Length: > 0 })
                        Array.Copy(cargo.Exports, ship.CargoExports, Math.Min(cargo.Exports.Length, ship.CargoExports.Length));
                    ship.TimerElapsed = cargo.Elapsed;
                    ship.TimerTotal = cargo.Total;
                }
            }
            catch (JsonException) { }
        }

        foreach (var q in snap.PortQuotas)
        {
            int pIdx = q.PortId - 1;
            if (pIdx is < 0 or >= VillageData.PortCount || q.QuotaIndex is < 0 or >= VillageData.QuotasPerPort) continue;
            var slot = state.PortQuotas[pIdx][q.QuotaIndex];
            slot.ExportRow = (byte)Math.Clamp(q.ResourceId, 0, VillageData.ExportCount - 1);
            slot.RequiredAmount = (uint)Math.Clamp(q.RequiredAmount, 0, 0x0fffffffL);
            slot.FulfilledAmount = (uint)Math.Clamp(q.FulfilledAmount, 0, 0x0fffffffL);
            slot.BonusPercent = q.BonusPercent;
        }

        foreach (var d in snap.MapDevices)
        {
            if (d.DeviceIndex is < 0 or >= VillageData.MapDeviceCount) continue;
            var dev = state.MapDevices[d.DeviceIndex];
            dev.State = d.State;
            dev.ActiveMapSlot = d.ActiveMapSlot;
            dev.MapStartedUtc = d.MapStartedUtc;
            dev.MapDurationSeconds = d.MapDurationSeconds;
            dev.PortalsRemaining = d.PortalsRemaining;
            dev.BossAmbushPending = d.BossAmbushPending;
            dev.RansomGold = d.RansomGold;
            try
            {
                var sum = JsonSerializer.Deserialize<MapSummaryBlob>(d.SummaryJson, JsonOpts);
                if (sum is not null)
                {
                    dev.TimerElapsed = sum.Elapsed;
                    dev.TimerTotal = sum.Total;
                    dev.StateFlags1 = sum.F1;
                    dev.StateFlags2 = sum.F2;
                    dev.MapCompletionPercents = sum.Percents ?? [];
                    dev.CurrentMapName = sum.CurrentMapName ?? "";
                    dev.SummaryJson = sum.Extra ?? "{}";
                }
            }
            catch (JsonException) { }
        }

        state.RewardItems.AddRange(snap.RewardItems);
        state.DisenchantQueue.AddRange(snap.DisenchantQueue);
        return state;
    }

    /// <summary>
    /// Applies a mutated <see cref="VillageStore.TownSnapshot"/> in place into this authoritative instance
    /// (caller must hold <see cref="Gate"/>) so sibling Operations mutations never replace the cached state or lock.
    /// </summary>
    public void ApplyFromStoreSnapshot(VillageStore.TownSnapshot snap)
    {
        var parsed = FromStoreSnapshot(snap);
        TreasuryGold = parsed.TreasuryGold;
        GoldBurnRemainder = parsed.GoldBurnRemainder;
        LastSimulatedUtc = parsed.LastSimulatedUtc;
        LastRerollUtc = parsed.LastRerollUtc;
        RerollCostGold = parsed.RerollCostGold;
        SuperRerollCostGold = parsed.SuperRerollCostGold;
        ShippingSeed = parsed.ShippingSeed;
        NextWorkerId = parsed.NextWorkerId;
        NextShipmentId = parsed.NextShipmentId;
        NextMapRunId = parsed.NextMapRunId;
        TutorialFlags = parsed.TutorialFlags;

        Array.Copy(parsed.Resources, Resources, Resources.Length);
        Array.Copy(parsed.ProductionProgress, ProductionProgress, ProductionProgress.Length);
        Array.Copy(parsed.FarmPlotCropResource, FarmPlotCropResource, FarmPlotCropResource.Length);

        Workers.Clear();
        Workers.AddRange(parsed.Workers);
        Recruits.Clear();
        Recruits.AddRange(parsed.Recruits);
        RecruitPoolFlag = parsed.RecruitPoolFlag;

        for (int i = 0; i < Ships.Length; i++)
        {
            Ships[i] = parsed.Ships[i];
            if (Ships[i].State == 1 && Ships[i].ArrivalUtc > Ships[i].DepartedUtc)
            {
                uint total = (uint)Math.Clamp(Ships[i].ArrivalUtc - Ships[i].DepartedUtc, 1, uint.MaxValue);
                Ships[i].TimerTotal = total;
            }
        }
        for (int p = 0; p < PortQuotas.Length; p++)
            for (int q = 0; q < PortQuotas[p].Length; q++)
                PortQuotas[p][q] = parsed.PortQuotas[p][q];

        for (int i = 0; i < MapDevices.Length; i++)
            MapDevices[i] = parsed.MapDevices[i];

        RewardItems.Clear();
        RewardItems.AddRange(parsed.RewardItems);
        DisenchantQueue.Clear();
        DisenchantQueue.AddRange(parsed.DisenchantQueue);

        if (DisenchantQueue.Count > 0)
        {
            DisenchantElapsed = (uint)Math.Clamp((long)Math.Floor(DisenchantQueue[0].ElapsedSeconds), 0, uint.MaxValue);
            DisenchantTotal = (uint)Math.Clamp((long)Math.Ceiling(DisenchantQueue[0].DurationSeconds), 0, uint.MaxValue);
        }
        else
        {
            DisenchantElapsed = 0;
            DisenchantTotal = 0;
        }

        NibbleLo = parsed.NibbleLo;
        NibbleHi = parsed.NibbleHi;
        ActiveRewardNotice = parsed.ActiveRewardNotice;

        ShipKidnapSlot1Based = 0;
        ShipKidnapFlag = 0;
        for (int i = 0; i < Ships.Length; i++)
        {
            if (Ships[i].BossPending || Ships[i].State == 3)
            {
                ShipKidnapSlot1Based = (byte)(i + 1);
                ShipKidnapFlag = 1;
                break;
            }
        }

        MapKidnapSlot1Based = 0;
        MapKidnapFlag = 0;
        for (int i = 0; i < MapDevices.Length; i++)
        {
            if (MapDevices[i].BossAmbushPending || MapDevices[i].State == 3)
            {
                MapKidnapSlot1Based = (byte)(i + 1);
                MapKidnapFlag = 1;
                break;
            }
        }
    }

    static (uint FirstKey, uint LastKey, List<InstanceVillage.WorkerSkill> Skills) ParseSkillsBlob(string json)
    {
        uint defaultFirst = VillageData.FirstNames.Count > 0 ? VillageData.FirstNames[0].Hash32 : 1u;
        uint defaultLast = VillageData.LastNames.Count > 0 ? VillageData.LastNames[0].Hash32 : 2u;
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return (defaultFirst, defaultLast, []);
        try
        {
            var blob = JsonSerializer.Deserialize<WorkerSkillsBlob>(json, JsonOpts);
            if (blob is not null)
            {
                var skills = blob.Skills?.Select(s => new InstanceVillage.WorkerSkill(s.J, s.R)).ToList() ?? [];
                return (blob.FirstWord != 0 ? blob.FirstWord : defaultFirst,
                    blob.LastWord != 0 ? blob.LastWord : defaultLast,
                    skills);
            }
        }
        catch (JsonException) { }
        return (defaultFirst, defaultLast, []);
    }
}

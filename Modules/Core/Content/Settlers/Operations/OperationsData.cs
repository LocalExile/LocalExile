using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Cached 3.29.3c <c>client.db</c> tables for Kingsmarch Operations: Shipping, Mapping, Disenchanting, and Recombinator.
/// Loaded once via <see cref="LoadGameData"/> with safe retail defaults when <c>client.db</c> is absent in unit tests.
/// </summary>
public static class OperationsData
{
    public const int ResourceCount = InstanceVillage.ResourceCount;      // 21 (0..20)
    public const int ExportCount = InstanceVillage.ExportCount;          // 16 (0..15)
    public const int PortCount = 6;                                      // VillageShippingPorts rows 1..6
    public const int ShipCount = InstanceVillage.ShipCount;              // 3 (0..2)
    public const int MapDeviceCount = InstanceVillage.MapDeviceCount;    // 3 (0..2)
    public const int QuotasPerPort = InstanceVillage.QuotasPerPort;      // 3 (0..2)

    public const int MagicDustResourceRow = 20;
    public const int MagicDustExportRow = 15;

    // VillageJobs.dat rows (verified in client.db)
    public const byte JobDisenchanting = 17;
    public const byte JobMappingFirst = 18;    // 18, 19, 20 for devices 0, 1, 2
    public const byte JobShippingFirst = 21;   // 21, 22, 23 for ships 0, 1, 2
    public const byte JobLostAtSeaFirst = 25;  // 25, 26, 27
    public const byte JobLostMappingFirst = 28;// 28, 29, 30

    // VillageJobTypes.dat rows
    public const byte JobTypeDisenchanting = 4;
    public const byte JobTypeMapping = 5;
    public const byte JobTypeShipping = 6;

    // ItemClasses.dat row for AtlasRelic (Idols) — receives >>1 gold / >>2 dust discount at 0x141d6e21b
    public const int AtlasRelicClassRow = 94;

    // Special T17 map base hash noted at 0x1424c6880
    public const uint SpecialTier17MapHash = 0x0df7cd54u;

    public sealed record ShippingPort(
        int Row,
        string Id,
        string Name,
        int DistanceFromPort,
        double RiskMultiplier,
        bool IsKarui,
        string ReturnMessage);

    public sealed record ShippingFailure(
        int Row,
        string Id,
        int[] ExportIndices,
        int ResourceLostPercent,
        int PortBonusLostPercent,
        int DelayPercent,
        int CrewMembersLost,
        int Severity,
        string SubText);

    public sealed record VillageExport(
        int Row,
        string Id,
        string Name,
        int ShippingValue,
        int ResourceRow);

    public sealed record MappingTier(
        int Tier,
        int BaseTime,
        int BaseDifficulty,
        int BasePower);

    public sealed record MappingBonusStat(
        uint StatId,
        int PercentChance);

    public sealed record LevelBalance(
        int Level,
        int GoldRespecCost,
        int RecombGoldBase,
        int RecombDustBase,
        int DisenchantModDustBase);

    public sealed record UniqueDisenchant(
        string Name,
        uint WordHash,
        float Value,
        float RuthlessValue);

    public sealed record RunegraftEntry(
        int Row,
        string Id,
        string Name,
        uint Hash);

    // Maps VillageExports row 0..15 -> VillageResources row 0..20.
    // Ores 0..4 -> Resources 5..9; Bars 5..9 -> Resources 15..19; Crops 10..14 -> Resources 10..14; Dust 15 -> Resource 20.
    public static readonly int[] DefaultExportToResource =
    [
        5, 6, 7, 8, 9,      // IronOre..VerisiumOre
        15, 16, 17, 18, 19, // RefinedIron..RefinedVerisium
        10, 11, 12, 13, 14, // Wheat..Orbisfera (Blue Zanthimum)
        20,                 // MagicDust
    ];

    static readonly int[] DefaultSkillLevelPowers = [12, 18, 27, 37, 49, 63];

    static readonly ShippingPort[] DefaultPorts =
    [
        new(0, "Kingsmarch", "Kingsmarch", 1, 1.0, false, ""),
        new(1, "RibenFell", "Riben Fell", 140, 0.75, false, "Ship has successfully returned from Riben Fell with rewards."),
        new(2, "NgakanuIsland", "Ngakanu", 186, 0.75, true, "Ship has successfully returned from Ngakanu with rewards."),
        new(3, "Pondium", "Pondium", 870, 1.0, false, "Ship has successfully returned from Pondium with rewards."),
        new(4, "TeOnui", "Te Onui", 939, 1.0, true, "Ship has successfully returned from Te Onui with rewards."),
        new(5, "Kalguuran", "Kalguur", 2317, 1.25, false, "Ship has successfully returned from Kalguur with rewards."),
        new(6, "MotiAro", "Moti Aro", 1143, 1.25, true, "Ship has successfully returned from Moti Aro with rewards."),
    ];

    static readonly VillageExport[] DefaultExports =
    [
        new(0, "IronOre", "Crimson Iron Ore", 4, 5),
        new(1, "CopperOre", "Orichalcum Ore", 5, 6),
        new(2, "Amber", "Petrified Amber Ore", 7, 7),
        new(3, "BismuthOre", "Bismuth Ore", 12, 8),
        new(4, "VerisiumOre", "Verisium Ore", 22, 9),
        new(5, "RefinedIron", "Crimson Iron Bar", 16, 15),
        new(6, "RefinedCopper", "Orichalcum Bar", 22, 16),
        new(7, "RefinedAmber", "Petrified Amber Bar", 30, 17),
        new(8, "RefinedBismuth", "Bismuth Bar", 50, 18),
        new(9, "RefinedVerisium", "Verisium Bar", 90, 19),
        new(10, "Wheat", "Wheat", 12, 10),
        new(11, "Corn", "Corn", 15, 11),
        new(12, "Pumpkin", "Pumpkin", 18, 12),
        new(13, "Globulora", "Orgourd", 21, 13),
        new(14, "Orbisfera", "Blue Zanthimum", 24, 14),
        new(15, "MagicDust", "Thaumaturgic Dust", 0, 20),
    ];

    static readonly ShippingFailure[] DefaultFailures =
    [
        new(0, "NULL", [], 0, 0, 0, 0, 0, ""),
        new(1, "ShipCommandeered", [], 0, 0, 0, 0, 2, "One of our ships has been commandeered!"),
        new(2, "Lose50%Metals", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9], 50, 0, 0, 0, 1, "Half of our shipment of ore has been lost!"),
        new(3, "Lose100%Metals", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9], 100, 0, 0, 0, 1, "Our shipment of ore has been lost!"),
        new(4, "Lose50%Plants", [10, 11, 12, 13, 14], 50, 0, 0, 0, 1, "Half of our shipment of crops has been lost!"),
        new(5, "Lose100%Plants", [10, 11, 12, 13, 14], 100, 0, 0, 0, 1, "Our shipment of crops has been lost!"),
        new(6, "Lose100%ArcaneDust", [15], 100, 0, 0, 0, 1, "Our shipment of Thaumaturgic Dust has been lost!"),
        new(7, "Lose50%AllResourcesOutgoing", [], 50, 0, 0, 0, 1, "Half of our entire shipment has been lost!"),
        new(8, "Lose50%AllResourcesAtPort", [], 50, 0, 0, 0, 1, "Half of our entire shipment has been lost!"),
        new(9, "Lose25%AllResourcesAtPort", [], 25, 0, 0, 0, 1, "A quarter of our entire shipment has been lost!"),
        new(10, "Lose75%AllResourcesOutgoing", [], 75, 0, 0, 0, 1, "Three quarters of our entire shipment has been lost!"),
        new(11, "Lose50%RewardsAtPort", [], 0, 50, 0, 0, 1, "Our expected returns have been halved!"),
        new(12, "Lose50%RewardsReturning", [], 0, 50, 0, 0, 1, "Our expected returns have been halved!"),
        new(13, "Lose25%RewardsAtPort", [], 0, 25, 0, 0, 1, "Our expected returns have been reduced by a quarter!"),
        new(14, "Lose75%RewardsReturning", [], 0, 75, 0, 0, 1, "Our expected returns have been reduced by three quarters!"),
        new(15, "LoseCrewMemberPubBrawl", [], 0, 0, 0, 1, 1, "One of our crewmembers has been lost!"),
        new(16, "LoseCrewMemberOverboardOutgoing", [], 0, 0, 0, 1, 1, "One of our crewmembers has been lost!"),
        new(17, "LoseCrewMemberOverboardReturning", [], 0, 0, 0, 1, 1, "One of our crewmembers has been lost!"),
        new(18, "ShipmentDelayed100%", [], 0, 0, 100, 0, 1, "Our ship has been delayed!"),
        new(19, "LoseAllRewardsOutgoing", [], 100, 0, 0, 0, 0, "Our entire shipment has been lost!"),
        new(20, "LoseAllRewardsReturning", [], 0, 100, 0, 0, 0, "Our entire shipment has been lost!"),
    ];

    static readonly MappingTier[] DefaultMappingTiers =
    [
        new(1, 2000, 25, 25),
        new(2, 2059, 30, 40),
        new(3, 2117, 35, 55),
        new(4, 2173, 40, 70),
        new(5, 2227, 45, 85),
        new(6, 2660, 50, 100),
        new(7, 2726, 55, 115),
        new(8, 2790, 60, 130),
        new(9, 2852, 65, 145),
        new(10, 2912, 70, 160),
        new(11, 3600, 75, 175),
        new(12, 3738, 80, 190),
        new(13, 3872, 85, 215),
        new(14, 4350, 90, 230),
        new(15, 4730, 95, 245),
        new(16, 5100, 100, 260),
        new(17, 5880, 125, 300),
    ];

    static readonly HashSet<int> DefaultRecombinableClassRows =
    [
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17,
        20, 21, 22, 23, 24, 25, 26, 32, 37, 56, 57, 94,
    ];

    static readonly HashSet<string> DefaultRecombinableClassIds = new(StringComparer.Ordinal)
    {
        "Amulet", "Ring", "Claw", "Dagger", "Wand",
        "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace",
        "Bow", "Staff", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace",
        "Quiver", "Belt", "Gloves", "Boots", "Body Armour", "Helmet", "Shield",
        "Sceptre", "FishingRod", "Rune Dagger", "Warstaff", "AtlasRelic",
    };

    static readonly HashSet<string> DisenchantableClasses = new(StringComparer.Ordinal)
    {
        "Amulet", "Ring", "Claw", "Dagger", "Wand",
        "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace",
        "Bow", "Staff", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace",
        "Quiver", "Belt", "Gloves", "Boots", "Body Armour", "Helmet", "Shield",
        "Sceptre", "Rune Dagger", "Warstaff", "Jewel", "AbyssJewel", "FishingRod",
    };

    public static ShippingPort[] Ports { get; private set; } = DefaultPorts;
    public static ShippingFailure[] Failures { get; private set; } = DefaultFailures;
    public static VillageExport[] Exports { get; private set; } = DefaultExports;
    public static int[] SkillLevelPowers { get; private set; } = DefaultSkillLevelPowers;
    public static MappingTier[] MappingTiers { get; private set; } = DefaultMappingTiers;
    public static MappingBonusStat[] MappingBonusStats { get; private set; } = [];
    public static LevelBalance[] BalanceByLevel { get; private set; } = BuildFallbackBalance();
    public static Dictionary<uint, UniqueDisenchant> UniqueDisenchantByWordHash { get; private set; } = [];
    public static Dictionary<string, UniqueDisenchant> UniqueDisenchantByName { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static HashSet<int> RecombinableClassRows { get; private set; } = [.. DefaultRecombinableClassRows];
    public static HashSet<string> RecombinableClassIds { get; private set; } = new(DefaultRecombinableClassIds, StringComparer.Ordinal);
    public static RunegraftEntry[] ActiveRunegrafts { get; private set; } = [];

    // VillageSharedConstants values
    public static uint RansomCost { get; private set; } = 50_000;
    public static int ExpectedShipmentValue { get; private set; } = 150;
    public static int ExpectedCrewValue { get; private set; } = 3;
    public static ulong MinShipmentValue { get; private set; } = 50_000;
    public static ulong MaxShipmentValue { get; private set; } = 50_000_000;
    public static int RecombinatorModDustCostExponentTimes100 { get; private set; } = 75;
    public static int RecombinatorModDustCostMultiplier { get; private set; } = 3;
    public static uint QuotaRerollCost { get; private set; } = 15_000;

    // VillageBaseStats defaults (3.29 maxed town)
    public static int DisenchantBonusPerQuality { get; private set; } = 2;
    public static int DisenchantBonusPerInfluence { get; private set; } = 50;
    public static int DisenchantBonusPerCorruptedImplicit { get; private set; } = 50;
    public static int DisenchantMaxRarity { get; private set; } = 3;
    public static int QuotaTier { get; private set; } = 10;
    public static int PortQuotaEffectivenessPct { get; private set; } = 0;
    public static int MappingModEffectPct { get; private set; } = 0;
    public static int MappingTier16EldritchOrMaven { get; private set; } = 1;
    public static int MappingMaxTier { get; private set; } = 17;

    static volatile bool _loaded;

    public static void EnsureLoaded()
    {
        if (!_loaded) LoadGameData();
    }

    public static void LoadGameData()
    {
        LoadConstants();
        LoadBaseStats();
        LoadSkillPowers();
        LoadExports();
        LoadPorts();
        LoadFailures();
        LoadMappingTiers();
        LoadMappingBonusStats();
        LoadBalanceByLevel();
        LoadUniqueDisenchantValues();
        LoadRecombinableClasses();
        LoadRunegrafts();
        _loaded = true;
    }

    public static int WorkerPowerForRank(int rank)
    {
        var powers = SkillLevelPowers;
        if (powers.Length == 0) return 12;
        return powers[Math.Clamp(rank, 0, powers.Length - 1)];
    }

    public static LevelBalance BalanceForLevel(int level)
    {
        var table = BalanceByLevel;
        if (table.Length == 0) return new(Math.Clamp(level, 1, 100), 100, 1000, 5000, 50);
        int idx = Math.Clamp(level, 1, table.Length) - 1;
        return table[idx];
    }

    public static MappingTier MappingTierFor(int tier)
    {
        var tiers = MappingTiers;
        if (tiers.Length == 0) return DefaultMappingTiers[0];
        return tiers[Math.Clamp(tier, 1, tiers.Length) - 1];
    }

    public static bool IsDisenchantableClass(string itemClass) =>
        !string.IsNullOrEmpty(itemClass) && DisenchantableClasses.Contains(itemClass);

    public static bool IsRecombinableClass(string itemClass) =>
        !string.IsNullOrEmpty(itemClass) && RecombinableClassIds.Contains(itemClass);

    public static bool IsRecombinableClassRow(int classRow) =>
        RecombinableClassRows.Contains(classRow);

    static readonly JsonSerializerOptions ItemJsonOptions = new();

    public static string SerializeItem(Item item, ModCatalog? mods) =>
        JsonSerializer.Serialize(CharacterSaves.ToSaved(item, mods), ItemJsonOptions);

    public static Item? DeserializeItem(string json, ItemCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<CharacterSaves.SavedItem>(json, ItemJsonOptions);
            return saved is null ? null : CharacterSaves.FromSaved(saved, catalog);
        }
        catch
        {
            return null;
        }
    }

    static void LoadConstants()
    {
        var table = ClientTables.TableOrEmpty("VillageSharedConstants");
        foreach (var r in table)
        {
            string id = r.TextOrNull("Id") ?? "";
            int val = r.IntOrNull("Value") ?? 0;
            switch (id)
            {
                case "RansomCost": if (val > 0) RansomCost = (uint)val; break;
                case "ExpectedShipmentValue": if (val > 0) ExpectedShipmentValue = val; break;
                case "ExpectedCrewValue": if (val > 0) ExpectedCrewValue = val; break;
                case "MinShipmentValue": if (val > 0) MinShipmentValue = (ulong)val; break;
                case "MaxShipmentValue": if (val > 0) MaxShipmentValue = (ulong)val; break;
                case "RecombinatorModDustCostExponentTimes100": if (val > 0) RecombinatorModDustCostExponentTimes100 = val; break;
                case "RecombinatorModDustCostMultiplier": if (val > 0) RecombinatorModDustCostMultiplier = val; break;
                case "QuotaRerollCost": if (val > 0) QuotaRerollCost = (uint)val; break;
            }
        }
    }

    static void LoadBaseStats()
    {
        var stats = ClientTables.TableOrEmpty("Stats");
        var table = ClientTables.TableOrEmpty("VillageBaseStats");
        foreach (var r in table)
        {
            int statRow = r.IntOrNull("Stat") ?? -1;
            int val = r.IntOrNull("StatValue") ?? 0;
            string statId = statRow >= 0 && statRow < stats.Count ? (stats[statRow].TextOrNull("Id") ?? "") : "";
            switch (statId)
            {
                case "village_disenchant_value_+%_per_quality": DisenchantBonusPerQuality = val; break;
                case "village_disenchant_value_+%_per_influence_type": DisenchantBonusPerInfluence = val; break;
                case "village_disenchant_value_+%_per_corrupted_implicit": DisenchantBonusPerCorruptedImplicit = val; break;
                case "village_can_disenchant_up_to_rarity": DisenchantMaxRarity = val; break;
                case "village_shipping_quota_tier": QuotaTier = Math.Max(1, val); break;
                case "village_port_quota_effectiveness_+%": PortQuotaEffectivenessPct = val; break;
                case "village_maps_have_mod_effect_+%": MappingModEffectPct = val; break;
                case "village_tier16_maps_have_eldritch_or_maven_influence": MappingTier16EldritchOrMaven = val; break;
                case "village_can_run_maps_up_to_tier": MappingMaxTier = Math.Clamp(val, 1, 17); break;
            }
        }
    }

    static void LoadSkillPowers()
    {
        var table = ClientTables.TableOrEmpty("VillageJobSkillLevels");
        if (table.Count == 0) return;
        var list = new int[table.Count];
        for (int i = 0; i < table.Count; i++)
            list[i] = table[i].IntOrNull("PlayerLevelRequired") ?? DefaultSkillLevelPowers[Math.Min(i, DefaultSkillLevelPowers.Length - 1)];
        SkillLevelPowers = list;
    }

    static void LoadExports()
    {
        var exportsTable = ClientTables.TableOrEmpty("VillageExports");
        if (exportsTable.Count == 0) return;

        var exportToResource = new int[exportsTable.Count];
        Array.Fill(exportToResource, -1);
        var resourcesTable = ClientTables.TableOrEmpty("VillageResources");
        foreach (var rr in resourcesTable)
        {
            if (rr.IntOrNull("Export") is int expRow && expRow >= 0 && expRow < exportToResource.Length)
                exportToResource[expRow] = rr.Index;
        }

        var list = new VillageExport[exportsTable.Count];
        for (int i = 0; i < exportsTable.Count; i++)
        {
            var r = exportsTable[i];
            int resRow = exportToResource[i] >= 0
                ? exportToResource[i]
                : (i < DefaultExportToResource.Length ? DefaultExportToResource[i] : MagicDustResourceRow);
            list[i] = new VillageExport(
                r.Index,
                r.TextOrNull("Id") ?? $"Export{i}",
                r.TextOrNull("Name") ?? $"Export {i}",
                r.IntOrNull("ShippingValue") ?? 0,
                resRow);
        }
        Exports = list;
    }

    static void LoadPorts()
    {
        var table = ClientTables.TableOrEmpty("VillageShippingPorts");
        if (table.Count == 0) return;
        var list = new ShippingPort[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            var r = table[i];
            double risk = r.RealOrNull("RiskMultiplier") ?? 1.0;
            bool isKarui = (r.IntOrNull("__col11") ?? 0) != 0;
            list[i] = new ShippingPort(
                r.Index,
                r.TextOrNull("Id") ?? $"Port{i}",
                r.TextOrNull("Name") ?? $"Port {i}",
                r.IntOrNull("DistanceFromPort") ?? 140,
                risk > 0 ? risk : 1.0,
                isKarui,
                r.TextOrNull("ReturnMessage") ?? "Ship has returned with rewards.");
        }
        Ports = list;
    }

    static void LoadFailures()
    {
        var table = ClientTables.TableOrEmpty("VillageShippingFailures");
        if (table.Count == 0) return;
        var list = new ShippingFailure[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            var r = table[i];
            list[i] = new ShippingFailure(
                r.Index,
                r.TextOrNull("Id") ?? $"Failure{i}",
                r.Ints("Resource").ToArray(),
                r.IntOrNull("ResourceLostPercent") ?? 0,
                r.IntOrNull("PortBonusLostPercent") ?? 0,
                r.IntOrNull("__col5") ?? 0,
                r.IntOrNull("CrewMembersLost") ?? 0,
                r.IntOrNull("__col7") ?? 0,
                r.TextOrNull("SubText") ?? "");
        }
        Failures = list;
    }

    static void LoadMappingTiers()
    {
        var table = ClientTables.TableOrEmpty("VillageMappingPerTier");
        if (table.Count == 0) return;
        var list = new MappingTier[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            var r = table[i];
            list[i] = new MappingTier(
                r.IntOrNull("Id") ?? (i + 1),
                r.IntOrNull("__col1") ?? 2000,
                r.IntOrNull("__col2") ?? 25,
                r.IntOrNull("__col3") ?? 25);
        }
        MappingTiers = list;
    }

    static void LoadMappingBonusStats()
    {
        var table = ClientTables.TableOrEmpty("VillageMappingBonusStats");
        if (table.Count == 0) return;
        var list = new List<MappingBonusStat>(table.Count);
        foreach (var r in table)
        {
            if (r.IntOrNull("Stats") is int statRow && statRow >= 0)
            {
                // Stat.g.cs IDs are 1-based (Stats._row + 1): e.g. _row 6957 -> Stat.MapAtlasInfluenceType (6958).
                list.Add(new MappingBonusStat((uint)(statRow + 1), r.IntOrNull("PercentChance") ?? 0));
            }
        }
        MappingBonusStats = [.. list];
    }

    static void LoadBalanceByLevel()
    {
        var table = ClientTables.TableOrEmpty("VillageBalancePerLevelShared");
        if (table.Count == 0) return;
        var list = new LevelBalance[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            var r = table[i];
            list[i] = new LevelBalance(
                r.IntOrNull("MaxAccountLevel") ?? (i + 1),
                r.IntOrNull("GoldRespecCost") ?? 0,
                r.IntOrNull("__col4") ?? 56,
                r.IntOrNull("__col5") ?? 2114,
                r.IntOrNull("__col6") ?? 18);
        }
        BalanceByLevel = list;
    }

    static void LoadUniqueDisenchantValues()
    {
        var table = ClientTables.TableOrEmpty("VillageUniqueDisenchantValues");
        var words = ClientTables.TableOrEmpty("Words");
        if (table.Count == 0 || words.Count == 0) return;

        var byHash = new Dictionary<uint, UniqueDisenchant>(table.Count);
        var byName = new Dictionary<string, UniqueDisenchant>(table.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in table)
        {
            if (r.IntOrNull("UniqueName") is not int wordRow || wordRow < 0 || wordRow >= words.Count) continue;
            var word = words[wordRow];
            string text = word.TextOrNull("Text") ?? "";
            uint hash = word.UIntOrNull("HASH32") ?? 0;
            float val = (float)(r.RealOrNull("Value") ?? 1.0);
            float ruthless = (float)(r.RealOrNull("RuthlessValue") ?? val);
            var entry = new UniqueDisenchant(text, hash, val, ruthless);
            if (hash != 0) byHash[hash] = entry;
            if (!string.IsNullOrEmpty(text)) byName[text] = entry;
        }
        UniqueDisenchantByWordHash = byHash;
        UniqueDisenchantByName = byName;
    }

    static void LoadRecombinableClasses()
    {
        var table = ClientTables.TableOrEmpty("RecombinableClasses");
        if (table.Count == 0) return;
        var classes = ClientTables.TableOrEmpty("ItemClasses");
        var rows = new HashSet<int>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in table)
        {
            if (r.IntOrNull("Id") is not int classRow || classRow < 0) continue;
            rows.Add(classRow);
            if (classRow < classes.Count && classes[classRow].TextOrNull("Id") is { Length: > 0 } classId)
                ids.Add(classId);
        }
        if (rows.Count > 0) RecombinableClassRows = rows;
        if (ids.Count > 0) RecombinableClassIds = ids;
    }

    static void LoadRunegrafts()
    {
        var baseTypes = ClientTables.TableOrEmpty("BaseItemTypes");
        if (baseTypes.Count == 0) return;
        var list = new List<RunegraftEntry>();
        int start = Math.Min(5250, baseTypes.Count);
        int end = Math.Min(5284, baseTypes.Count - 1);
        for (int row = start; row <= end; row++)
        {
            var r = baseTypes[row];
            string id = r.TextOrNull("Id") ?? "";
            string name = r.TextOrNull("Name") ?? "";
            if (!id.Contains("Runegraft", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("[DNT", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name)) continue;
            uint hash = r.UIntOrNull("HASH32") ?? 0;
            if (hash == 0) hash = ItemCatalog.ComputeHash32(id);
            list.Add(new RunegraftEntry(row, id, name, hash));
        }
        ActiveRunegrafts = [.. list];
    }

    static LevelBalance[] BuildFallbackBalance()
    {
        var arr = new LevelBalance[100];
        for (int lvl = 1; lvl <= 100; lvl++)
        {
            int gold = (int)Math.Round(56 * Math.Pow(1.055, lvl - 1));
            int dust = (int)Math.Round(2114 * Math.Pow(1.049, lvl - 1));
            int disenchant = (int)Math.Round(18 * Math.Pow(1.05, lvl - 1));
            arr[lvl - 1] = new LevelBalance(lvl, Math.Max(4, gold / 2), Math.Min(10326, gold), Math.Min(235223, dust), Math.Min(2130, disenchant));
        }
        return arr;
    }
}

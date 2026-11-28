using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.MapOres;

/// <summary>The six map ore-slot encounter types plus Lost Shipments and Starfall Crater.</summary>
public enum MapOreSlotKind : byte
{
    CrimsonIron = 0,
    Orichalcum = 1,
    PetrifiedAmber = 2,
    Bismuth = 3,
    Verisium = 4,
    DustHourglass = 5,
    LostShipment = 6,
    StarfallCrater = 7,
}

/// <summary>
/// Static catalog and client-table loader for Settlers map-side ore encounters, Lost Shipments,
/// Starfall Crater entrance, Runegraft drops, and exact 3.29.3c <c>.ot</c> component / StateMachine layouts.
/// </summary>
public static class MapOreTables
{
    // ---- Terrain & Object Metadata Paths (verified against ore_node.arm and .ot chains in game-files.zip) ----

    public const string OreNodeMarker = "Metadata/Terrain/Leagues/Settlers/Node/Objects/OreNode";
    public const string ClaimedMarker = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/MineralClaimedMarker";

    public const string CrimsonIronObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/CrimsonIron";
    public const string DemonCopperObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/DemonCopper";
    public const string CopperAltarObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/DemonCopperObjects/copper_altar";
    public const string CopperSpawner1Object = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/DemonCopperObjects/CopperSpawner1";
    public const string CopperSpawner2Object = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/DemonCopperObjects/CopperSpawner2";
    public const string PetrifiedWoodObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/PetrifiedWood";
    public static readonly string[] FungalSpawnerObjects =
    [
        "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/PetrifiedWoodObjects/FungalSpawner1",
        "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/PetrifiedWoodObjects/FungalSpawner2",
        "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/PetrifiedWoodObjects/FungalSpawner3",
        "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/PetrifiedWoodObjects/FungalSpawner4",
        "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/PetrifiedWoodObjects/FungalSpawner5",
    ];
    public const string BismuthObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/Bismuth";
    public const string VerisiumObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/Verisium";
    public const string HourglassObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/Hourglass";
    public const string HourglassAltarObject = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/hourglass_altar";

    public const string LostShipmentChestObject = "Metadata/Chests/LostShipmentChest/LostShipmentChest";
    public const string KalguuranShipmentModel = "Metadata/Chests/LostShipmentChest/KalguuranLostShipmentChest.ao";
    public const string KaruiShipmentModel = "Metadata/Chests/LostShipmentChest/KaruiLostShipmentChest.ao";

    public const string StarfallEntranceObject = "Metadata/Terrain/Leagues/Settlers/Objects/VerisiumBossSubAreaEntrance";
    public const string StarfallTransitionObject = "Metadata/Terrain/Leagues/Settlers/Objects/VerisiumBossSubAreaTransition";
    public const string StarfallTransitionModel = "Metadata/Terrain/Doodads/Leagues/Settlers/VerisiumBossEntrance.ao";
    public const string StarfallAreaId = "SettlersBossFallenStar";
    public const ushort StarfallAreaHash16 = 37119; // WorldAreas row 1919

    public const string ProspectingAstrolabeId = "Metadata/Items/Currency/AstrolabeSettlers";

    // ---- Exact deserialized component lists & StateMachine indices (ot_components.py / 3.29.3c) ----

    public static readonly string[] ClaimedMarkerComponents =
        ["Positioned", "BaseEvents", "Animated", "StateMachine", "MinimapIcon"];
    public const byte ClaimedMarkerSmIndex = 3;
    public static readonly string[] ClaimedMarkerStates = ["claimed", "activated", "light_colour"];

    public static readonly string[] CrimsonIronComponents =
        ["Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction", "StateMachine", "ControlZone", "Functions"];
    public const byte OreNodeSmIndex = 5;
    public static readonly string[] CrimsonIronStates = ["activated", "in_progress", "ore_empty", "interactable", "growths_alive"];

    public static readonly string[] DemonCopperComponents =
        ["Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction", "StateMachine", "ControlZone"];
    public static readonly string[] DemonCopperStates = ["activated", "in_progress", "ore_empty", "encounter", "type", "lights"];

    public static readonly string[] CopperAltarComponents =
        ["Positioned", "BaseEvents", "StateMachine", "Animated", "InteractionAction"];
    public const byte AltarSmIndex = 2;
    public static readonly string[] AltarStates = ["encounter", "type", "claimed"];

    public static readonly string[] CopperSpawnerComponents =
        ["Positioned", "BaseEvents", "TriggerableBlockage", "Animated", "Functions"];

    public static readonly string[] PetrifiedWoodComponents =
        ["Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction", "StateMachine", "ControlZone", "TriggerableBlockage"];
    public static readonly string[] PetrifiedWoodStates = ["activated", "in_progress", "ore_empty", "am_dead"];

    public static readonly string[] FungalSpawnerComponents =
        ["Positioned", "BaseEvents", "Animated"];

    public static readonly string[] BismuthComponents =
        ["Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction", "StateMachine", "ControlZone", "Buffs", "Functions"];
    public static readonly string[] BismuthStates = ["activated", "in_progress", "ore_empty", "effect_off"];

    public static readonly string[] VerisiumComponents =
        ["Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction", "StateMachine", "ControlZone"];
    public static readonly string[] VerisiumStates =
        ["activated", "in_progress", "ore_empty", "encounter", "begun_channelling", "channelling", "in_zone"];

    public static readonly string[] HourglassComponents = DemonCopperComponents;
    public static readonly string[] HourglassStates = DemonCopperStates;
    public static readonly string[] HourglassAltarComponents = CopperAltarComponents;

    public static readonly string[] LostShipmentComponents =
        ["Positioned", "BaseEvents", "Animated", "Chest", "Stats", "ObjectMagicProperties", "InteractionAction", "MinimapIcon"];

    public static readonly string[] StarfallEntranceComponents =
        ["Positioned", "Animated", "TriggerableBlockage"];

    /// <summary>Exact 8 relative marker offsets from OreNode (34, 35) in <c>ore_node.arm</c>.</summary>
    public static readonly (int Dx, int Dy)[] RelativeMarkerOffsets =
    [
        (12, 22), (24, 11), (23, -12), (-11, -24),
        (-23, -13), (-23, 10), (-10, 22), (12, -24),
    ];

    // ---- BuffDefinitions rows (verified in client.db) ----

    /// <summary>K'Tash's Anger (<c>demon_copper_meteors</c>) — Fire Orichalcum player buff.</summary>
    public const ushort BuffOrichalcumFire = 2491;
    /// <summary>Beidat's Wrath (<c>demon_copper_lightning</c>) — Lightning Orichalcum player buff.</summary>
    public const ushort BuffOrichalcumLightning = 2492;
    /// <summary><c>bismuth_epk</c> — Bismuth elemental empowerment visual on map Magic/Rare monsters.</summary>
    public const ushort BuffBismuthEmpowered = 2528;
    /// <summary>Verisium Energy (<c>verisium_channel_shield</c>) — invulnerable &amp; stun-immune while channelling Verisium.</summary>
    public const ushort BuffVerisiumChannelShield = 2718;

    // ---- Monster Varieties (verified in client.db & .ot Preload blocks) ----

    public const string CrimsonPodMap = "Metadata/Monsters/LeagueKalguur/CrimsonIron/SmallGrowthMaps";
    public const string CrimsonPodCampaign = "Metadata/Monsters/LeagueKalguur/CrimsonIron/SmallGrowth";
    public static readonly string[] CrimsonFodder =
    [
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshFodder1CrimsonIron",
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshFodder2CrimsonIron",
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshFodder3CrimsonIron",
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshFodder4CrimsonIron",
    ];
    public static readonly string[] CrimsonElites =
    [
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshElite1CrimsonIron",
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshElite2CrimsonIron",
    ];

    public static readonly string[] OrichalcumDemonFodder =
    [
        "Metadata/Monsters/LeagueKalguur/DemonFaction/DemonCopperDemonFodder1_",
        "Metadata/Monsters/LeagueKalguur/DemonFaction/DemonCopperDemonFodder2_",
        "Metadata/Monsters/LeagueKalguur/DemonFaction/DemonCopperDemonFodder3",
    ];
    public static readonly string[] OrichalcumDemonElites =
    [
        "Metadata/Monsters/LeagueKalguur/DemonFaction/DemonCopperDemonElite1",
        "Metadata/Monsters/LeagueKalguur/DemonFaction/DemonCopperDemonElite2_",
    ];
    public static readonly string[] OrichalcumPaleFodder =
    [
        "Metadata/Monsters/LeagueKalguur/PaleFaction/DemonCopperPaleFodder1",
        "Metadata/Monsters/LeagueKalguur/PaleFaction/DemonCopperPaleFodder2",
        "Metadata/Monsters/LeagueKalguur/PaleFaction/DemonCopperPaleFodder3",
    ];
    public static readonly string[] OrichalcumPaleElites =
    [
        "Metadata/Monsters/LeagueKalguur/PaleFaction/DemonCopperPaleElite1",
        "Metadata/Monsters/LeagueKalguur/PaleFaction/DemonCopperPaleElite2",
    ];

    public const string AmberTreeTarget = "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodPunchingBag";
    public static readonly string[] AmberAttackers =
    [
        "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodFungusZombie_",
        "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodPrimordialMonster2",
        "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodPrimordialMonster3__",
        "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodFallenSplit",
    ];
    public static readonly string[] AmberElites =
    [
        "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodPrimordialMonster1",
        "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodTendrilSentinel",
    ];

    public static readonly string[] BismuthTornados =
    [
        "Metadata/Monsters/InvisibleFire/BismuthMineralTornadoFire_",
        "Metadata/Monsters/InvisibleFire/BismuthMineralTornadoCold",
        "Metadata/Monsters/InvisibleFire/BismuthMineralTornadoLightning",
    ];

    public static readonly string[] VerisiumFodder =
    [
        "Metadata/Monsters/LeagueCrucible/Fire/CityStalker",
        "Metadata/Monsters/LeagueCrucible/Fire/Grappler",
        "Metadata/Monsters/LeagueCrucible/Cold/FlameBearer",
        "Metadata/Monsters/LeagueCrucible/Cold/WickerMan",
        "Metadata/Monsters/LeagueCrucible/Lightning/SeaWitch",
        "Metadata/Monsters/LeagueCrucible/Lightning/WaterElemental",
        "Metadata/Monsters/LeagueCrucible/Physical/Devourer",
        "Metadata/Monsters/LeagueCrucible/Chaos/LargeSkeleton",
    ];
    public static readonly string[] VerisiumElites =
    [
        "Metadata/Monsters/LeagueCrucible/Fire/ClockworkGolem",
        "Metadata/Monsters/LeagueCrucible/Fire/FourLeggedDemon",
        "Metadata/Monsters/LeagueCrucible/Cold/KaruiElder",
        "Metadata/Monsters/LeagueCrucible/Lightning/Vendigo_",
        "Metadata/Monsters/LeagueCrucible/Physical/BasiliskSnake",
        "Metadata/Monsters/LeagueCrucible/Chaos/SkeletonBeast",
    ];
    public static readonly string[] VerisiumUniques =
    [
        "Metadata/Monsters/LeagueCrucible/Cold/PhoenixBoss_",
        "Metadata/Monsters/LeagueCrucible/Lightning/SaplingBoss",
        "Metadata/Monsters/LeagueCrucible/Chaos/BoneGolemBoss",
        "Metadata/Monsters/LeagueCrucible/Physical/SinsSpiderBoss",
    ];

    public static readonly string[] HourglassFodder =
    [
        "Metadata/Monsters/FaridunLeague/FaridunSwarm/FaridunSwarmSettlers_",
        "Metadata/Monsters/FaridunLeague/FaridunZombie/FaridunZombieSettlers",
        "Metadata/Monsters/FaridunLeague/FaridunHumanDaggersSettlers",
        "Metadata/Monsters/FaridunLeague/FaridunHumanShieldSettlers",
    ];
    public static readonly string[] HourglassElites =
    [
        "Metadata/Monsters/FaridunLeague/FaridunFleshHulk/FaridunFleshHulkSettlers__",
        "Metadata/Monsters/FaridunLeague/FaridunHumanShieldSettlers",
    ];

    // ---- Loaded Table Data ----

    /// <summary>Base ore amount per area level (1..100) from <c>VillageBalancePerLevelShared.__col6</c>.</summary>
    static int[] _oreBaseByLevel = BuildFallbackOreCurve();
    /// <summary>Base dust/gold scale per area level (1..100) from <c>VillageBalancePerLevelShared.__col4</c>.</summary>
    static int[] _dustBaseByLevel = BuildFallbackDustCurve();
    /// <summary>Monster more-life % by area level (1..100) from <c>SettlersLifeScalingPerLevel</c>.</summary>
    static int[] _lifeScaleByLevel = new int[101];
    /// <summary>Base ore selection weights [Crimson, Orichalcum, Amber, Bismuth, Verisium] from <c>VillageExports.__col5</c>.</summary>
    static int[] _oreWeights = [200, 175, 133, 84, 52];
    /// <summary>Valid 3.29 Runegraft item Ids (excluding <c>[DNT - UNUSED]</c> rows).</summary>
    static IReadOnlyList<string> _runegrafts = [];
    /// <summary>Unique item disenchant dust values keyed by unique Words HASH32 (from <c>VillageUniqueDisenchantValues</c>).</summary>
    static Dictionary<uint, double> _uniqueDustByWordHash = [];

    public static bool Loaded { get; private set; }
    public static IReadOnlyList<string> Runegrafts => _runegrafts;

    public static void LoadGameData()
    {
        var oreCurve = BuildFallbackOreCurve();
        var dustCurve = BuildFallbackDustCurve();
        if (ClientTables.Has("VillageBalancePerLevelShared"))
        {
            foreach (var row in ClientTables.Table("VillageBalancePerLevelShared"))
            {
                int level = row.IntOrNull("MaxAccountLevel") ?? (row.Index + 1);
                if (level is < 1 or > 100) continue;
                if (row.IntOrNull("__col6") is int ore && ore > 0) oreCurve[level] = ore;
                if (row.IntOrNull("__col4") is int dust && dust > 0) dustCurve[level] = dust;
            }
        }
        _oreBaseByLevel = oreCurve;
        _dustBaseByLevel = dustCurve;

        var lifeScale = new int[101];
        if (ClientTables.Has("SettlersLifeScalingPerLevel"))
        {
            foreach (var row in ClientTables.Table("SettlersLifeScalingPerLevel"))
            {
                int level = row.IntOrNull("Level") ?? (row.Index + 1);
                if (level is < 1 or > 100) continue;
                if (row.IntOrNull("MoreLife") is int ml) lifeScale[level] = ml;
            }
        }
        _lifeScaleByLevel = lifeScale;

        int[] weights = [200, 175, 133, 84, 52];
        if (ClientTables.Has("VillageExports"))
        {
            var exportRows = ClientTables.Table("VillageExports").ToList();
            for (int i = 0; i < 5 && i < exportRows.Count; i++)
                if (exportRows[i].IntOrNull("__col5") is int w && w > 0) weights[i] = w;
        }
        _oreWeights = weights;

        var runegrafts = new List<string>();
        if (ClientTables.Has("BaseItemTypes"))
        {
            foreach (var row in ClientTables.Table("BaseItemTypes"))
            {
                string id = row.Text("Id") ?? "";
                string name = row.Text("Name") ?? "";
                if (id.StartsWith("Metadata/Items/Currency/Runegraft", StringComparison.Ordinal)
                    && !name.Contains("[DNT", StringComparison.OrdinalIgnoreCase)
                    && !id.Equals("Metadata/Items/Currency/RunegraftTest", StringComparison.Ordinal))
                    runegrafts.Add(id);
            }
        }
        _runegrafts = runegrafts;

        var uniqueDust = new Dictionary<uint, double>();
        if (ClientTables.Has("VillageUniqueDisenchantValues") && ClientTables.Has("Words"))
        {
            var words = ClientTables.Table("Words");
            foreach (var row in ClientTables.Table("VillageUniqueDisenchantValues"))
            {
                if (row.IntOrNull("UniqueName") is not int wRow || words.Row(wRow) is not { } wordRow) continue;
                uint wordHash = wordRow.UIntOrNull("HASH32") ?? 0;
                double val = row.Real("Value");
                if (wordHash != 0 && val > 0) uniqueDust[wordHash] = val;
            }
        }
        _uniqueDustByWordHash = uniqueDust;
        Loaded = true;
    }

    public static int BaseOreAmount(int areaLevel) => _oreBaseByLevel[Math.Clamp(areaLevel, 1, 100)];
    public static int BaseDustScale(int areaLevel) => _dustBaseByLevel[Math.Clamp(areaLevel, 1, 100)];
    public static int MonsterMoreLifePct(int areaLevel) => _lifeScaleByLevel[Math.Clamp(areaLevel, 1, 100)];
    public static int BaseOreWeight(MapOreSlotKind kind) => (int)kind is >= 0 and < 5 ? _oreWeights[(int)kind] : 0;

    public static double? UniqueDisenchantBaseValue(IReadOnlyList<uint> nameWords)
    {
        for (int i = 0; i < nameWords.Count; i++)
            if (_uniqueDustByWordHash.TryGetValue(nameWords[i], out double val))
                return val;
        return null;
    }

    public static VillageResourceKind PendingResourceOf(MapOreSlotKind kind) => kind switch
    {
        MapOreSlotKind.CrimsonIron => VillageResourceKind.PendingIronOre,
        MapOreSlotKind.Orichalcum => VillageResourceKind.PendingCopperOre,
        MapOreSlotKind.PetrifiedAmber => VillageResourceKind.PendingAmber,
        MapOreSlotKind.Bismuth => VillageResourceKind.PendingBismuthOre,
        MapOreSlotKind.Verisium => VillageResourceKind.PendingVerisiumOre,
        MapOreSlotKind.DustHourglass => VillageResourceKind.MagicDust,
        _ => VillageResourceKind.PendingIronOre,
    };

    public static VillageResourceKind RefinedBarOf(MapOreSlotKind kind) => kind switch
    {
        MapOreSlotKind.CrimsonIron => VillageResourceKind.RefinedIron,
        MapOreSlotKind.Orichalcum => VillageResourceKind.RefinedCopper,
        MapOreSlotKind.PetrifiedAmber => VillageResourceKind.RefinedAmber,
        MapOreSlotKind.Bismuth => VillageResourceKind.RefinedBismuth,
        MapOreSlotKind.Verisium => VillageResourceKind.RefinedVerisium,
        MapOreSlotKind.DustHourglass => VillageResourceKind.MagicDust,
        _ => VillageResourceKind.RefinedIron,
    };

    public static string DisplayName(MapOreSlotKind kind, bool refined = false) => (kind, refined) switch
    {
        (MapOreSlotKind.CrimsonIron, false) => "Crimson Iron Ore",
        (MapOreSlotKind.CrimsonIron, true) => "Crimson Iron Bars",
        (MapOreSlotKind.Orichalcum, false) => "Orichalcum Ore",
        (MapOreSlotKind.Orichalcum, true) => "Orichalcum Bars",
        (MapOreSlotKind.PetrifiedAmber, false) => "Petrified Amber Ore",
        (MapOreSlotKind.PetrifiedAmber, true) => "Petrified Amber Bars",
        (MapOreSlotKind.Bismuth, false) => "Bismuth Ore",
        (MapOreSlotKind.Bismuth, true) => "Bismuth Bars",
        (MapOreSlotKind.Verisium, false) => "Verisium Ore",
        (MapOreSlotKind.Verisium, true) => "Verisium Bars",
        (MapOreSlotKind.DustHourglass, _) => "Thaumaturgic Dust",
        (MapOreSlotKind.LostShipment, _) => "Lost Shipment",
        (MapOreSlotKind.StarfallCrater, _) => "Starfall Crater",
        _ => "Ore",
    };

    public static string CropDisplayName(VillageResourceKind crop) => crop switch
    {
        VillageResourceKind.Wheat => "Wheat",
        VillageResourceKind.Corn => "Corn",
        VillageResourceKind.Pumpkin => "Pumpkin",
        VillageResourceKind.Globulora => "Orgourd",
        VillageResourceKind.Orbisfera => "Blue Zanthimum",
        _ => crop.ToString(),
    };

    public static StateMachineData MakeStates(string[] names, params (string Key, int Value)[] overrides)
    {
        var data = new StateMachineData(new int[names.Length], 0, names);
        foreach (var (k, v) in overrides) data = data.Set(k, v);
        return data;
    }

    public static MonsterCatalog.Variety? PickSupported(IReadOnlyList<string> candidates, Random rng)
    {
        var supported = new List<MonsterCatalog.Variety>(candidates.Count);
        foreach (string id in candidates)
            if (Monsters.Find(id) is { Supported: true } v) supported.Add(v);
        return supported.Count == 0 ? null : supported[rng.Next(supported.Count)];
    }

    static int[] BuildFallbackOreCurve()
    {
        var arr = new int[101];
        for (int lvl = 1; lvl <= 100; lvl++)
            arr[lvl] = Math.Max(18, (int)Math.Round(18.0 * Math.Pow(1.049, lvl - 1)));
        return arr;
    }

    static int[] BuildFallbackDustCurve()
    {
        var arr = new int[101];
        for (int lvl = 1; lvl <= 100; lvl++)
            arr[lvl] = Math.Max(56, (int)Math.Round(56.0 * Math.Pow(1.063, lvl - 1)));
        return arr;
    }
}

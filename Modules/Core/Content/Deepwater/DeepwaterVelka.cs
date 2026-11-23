using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>
/// Kishara's Rest (<c>DeepwaterRooms#29</c> → <c>#30</c>), the two-phase encounter with Cressie, Tamed Behemoth
/// (<c>MonsterVarieties#10582</c>) and Velka, the Tide Witch (<c>MonsterVarieties#10583</c>), her drops, and the
/// Velka Bloodline (<c>Descendancy#15 Brinerot</c> via <c>VelkaDescensionObject</c>).
/// <para>Room <c>#29</c> (<c>Sand_CrashedShip_01.arm</c>, pre-imbuement / Chart): places the permanent
/// <c>VelkaFlameBarrier</c> (<c>Transitionable = 1</c>, blocked) and <c>VelkaPreFight</c> (<c>lantern = 1</c>);
/// approaching <c>VelkaWaterWallSeenTrigger</c> sets <c>QF 3612</c> (<c>DeepwaterSeeVelkaWaterWall</c>) and plays
/// <c>Velka_Taunt</c> (<c>active = 1</c>).</para>
/// <para>Room <c>#30</c> (<c>Sand_CrashedShip_Arena.arm</c>, imbued Voyage level ≥ 83): places
/// <c>VelkaFlameBarrierFight</c>, <c>ArenaTeleporter</c> (3.29.1 arena re-entry), <c>InvisibleLantern</c> (no-drown
/// safe zone), <c>VelkaPreFight</c>, left/right pool locators, and <c>VelkaDescensionObject</c> with its <c>.arm</c>
/// model override (<c>anchor_slam_descendency_object.ao</c>). Crossing into the arena seals the barrier, activates
/// <c>ArenaTeleporter</c>, sets <c>QF 3609</c>, and spawns Cressie with mounted Velka (<c>rider = 1</c>). At 60%
/// Cressie life she dismounts (<c>rider = 2</c>) and Velka spawns; if Cressie dies first, Velka enters a bounded
/// Stormcall (<c>GE#7789</c>) enrage (<c>eel_death = 1</c>). Defeating both reopens the barrier, sets <c>QF 3608</c>,
/// <c>QF 3624</c> and <c>LQF 1886</c>, drops Velka's guaranteed unique, 8% <c>Coursing Current Support</c>, and
/// first-kill quest items, and activates <c>VelkaDescensionObject</c> (<c>enabled = 1, visible = 1</c>).</para>
/// </summary>
public static class DeepwaterVelka
{
    /// <summary><c>DeepwaterRooms</c> row <c>29</c> (<c>SandyCrashedShip1</c>, pre-imbuement barrier) and row <c>30</c>
    /// (<c>SandyCrashedShipArena</c>, boss arena).</summary>
    public const int BarrierRoom = 29, ArenaRoom = 30;
    /// <summary>Minimum area level for Kishara's Rest (<c>#29</c>) on Sandy Charts and for the imbued Voyage boss arena (<c>#30</c>).</summary>
    public const int MinChartRoomLevel = 81, MinArenaLevel = 83;

    // Room #29 / #30 object metadata types (Sand_CrashedShip_01.arm & Sand_CrashedShip_Arena.arm).
    public const string WaterWallTriggerType = "Metadata/MiscellaneousObjects/Deepwater/VelkaWaterWallSeenTrigger";
    public const string BarrierType = "Metadata/NPC/League/Deepwater/VelkaFlameBarrier";
    public const string FightBarrierType = "Metadata/NPC/League/Deepwater/VelkaFlameBarrierFight";
    public const string PreFightType = "Metadata/NPC/League/Deepwater/VelkaPreFight";
    public const string ArenaTeleporterType = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/ArenaTeleporter";
    public const string InvisibleLanternType = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/InvisibleLantern";
    public const string DescensionObjectType = "Metadata/MiscellaneousObjects/Descendancy/VelkaDescensionObject";
    public const string RightEelLocatorType = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/RightEelLocator";
    public const string LeftEelLocatorType = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/LeftEelLocator";
    public const string CentreEelLocatorType = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/CentreEelLocator";

    // Exact .ao model paths from Sand_CrashedShip_01.arm & Sand_CrashedShip_Arena.arm (VelkaDescensionObject MUST override
    // its .ot default SareshBloodlineAscendancy.ao; Barrier/PreFight have no animated_object on their .ot).
    public const string BarrierModel = "Metadata/Effects/Spells/monsters_effects/League_Deepwater/DeepwaterBoss/arenaBarrier.ao";
    public const string PreFightModel = "Metadata/Monsters/LeagueDeepwater/DeepwaterBoss/DeepwaterBossNoClaws.ao";
    public const string DescensionModel = "Metadata/Monsters/LeagueDeepwater/DeepwaterBoss/anchor_slam_descendency_object.ao";
    public const string TeleporterModel = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/generic_cylinder_deepwater_arena.ao";
    public const string InvisibleLanternModel = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterBossArena/DeepwaterBossArenaBlank.ao";

    /// <summary>Boss <c>MonsterVarieties</c> rows and metadata paths.</summary>
    public const int CressieRow = 10582, VelkaRow = 10583;
    public const string CressieVariety = "Metadata/Monsters/DeepwaterLeague/EelBoss";
    public const string VelkaVariety = "Metadata/Monsters/DeepwaterLeague/EelBossRider";
    /// <summary><c>GrantedEffects#7789</c> (<c>GSEelBossStormcallLightningBolt</c>): Velka's Cressie-death enrage strikes.</summary>
    public const int StormcallEffect = 7789;

    // QuestFlags (Quest#211 Turning the Tide & quest items).
    public const string WaterWallSeenFlag = "DeepwaterSeeVelkaWaterWall";         // QF 3612
    public const string VesperPowerGivenFlag = "DeepwaterVesperPowerGiven";       // QF 3610
    public const string VelkaSeenFlag = "DeepwaterVelkaSeen";                     // QF 3609
    public const string VelkaDefeatedJustNowFlag = "DeepwaterVelkaDefeatedJustNow"; // QF 3608
    public const string VelkaDefeatedFlag = "DeepwaterVelkaDefeated";             // QF 3624
    public const string HaveBossAllflameFlag = "HaveDeepwaterBossAllflame";       // QF 3607
    public const string HaveUpgradePiece5Flag = "HaveDeepwaterUpgradeItem5";      // QF 3567
    public const string HaveQuestDucat3Flag = "HaveDeepwaterQuestDucat3";         // QF 3631

    // LeagueQuestFlags.
    public const int UpgradePiece5TurnedInLeagueFlag = 1828;     // TurnInDeepwaterUpgradeItem5
    public const int VelkaFlameGivenLeagueFlag = 1885;           // DeepwaterVelkaFlameGiven
    public const int VelkaKilledLeagueFlag = 1886;               // DeepwaterVelkaKilled
    public const int VesperPowerGivenLeagueFlag = 1889;          // DeepwaterVesperPowerGivenLeague

    /// <summary>Velka Bloodline (<c>Descendancy#15 Brinerot</c>).</summary>
    public const string BloodlineId = "Brinerot";
    public const byte BloodlineRow = 15;

    // Boss drops (BaseItemTypes & Mods).
    public const string CoursingCurrentSupportId = "Metadata/Items/Gems/SupportGemCrustaceousGrasp"; // #2457
    public const string BossAllflameId = "Metadata/Items/QuestItems/BossAllflameDeepwater";          // #1537
    public const string ForbiddenScriptureId = "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece5"; // #5430
    public const string TsoathaDucatId = "Metadata/Items/QuestItems/Deepwater/DeepwaterQuestDucat3"; // #5433
    public const string PearlSupportTwoModId = "UniquePearlSupportTwo";                              // Mods#40344

    /// <summary>Guaranteed Velka unique drop weights (sum = 100; ignores IIQ/IIR). <c>Stormblood</c> splits across
    /// Sapphire Flask (8%) and Topaz Flask (8%); <c>Pearl of Tsoatha</c> (14%) rolls 10/14 1-support and 4/14 2-support.</summary>
    public static readonly (string Name, int Weight)[] UniqueDropWeights =
    [
        ("Seablister", 35),
        ("The Crustacean's Call", 35),
        ("Stormblood Sapphire Flask", 8),
        ("Stormblood Topaz Flask", 8),
        ("Pearl of Tsoatha", 14),
    ];

    /// <summary>Independent chance (%) for Velka to drop <c>Coursing Current Support</c> (ignores IIQ/IIR).</summary>
    public const int CoursingCurrentChancePercent = 8;

    // G: trigger distances, phase timings, pool swap cadence, and enrage stormcall parameters.
    const int WaterWallTriggerRange = 45;     // G: proximity to VelkaWaterWallSeenTrigger in Room #29
    const int ArenaEngageRange = 55;          // G: proximity to CentreEelLocator to start the fight in Room #30
    const int ArenaSafeMinRadius = 120;       // G: covers entire arena from barrier (410,363) to altar (478,527) around InvisibleLantern (446,456)
    const int DismountThresholdPercent = 60;  // Cressie HP % that triggers Phase 2 dismount
    const double IntroSeconds = 2.0;          // G: VelkaPreFight lantern=2 / active=2 intro before Cressie emerges
    const double DismountSeconds = 1.5;       // G: brief invulnerable dismount beat before both fight
    const double PoolSwapInterval = 14.0;     // G: Cressie pool swap cooldown between Right and Left locators
    const double EnrageGraceSeconds = 3.0;    // G: bounded invulnerable stormcall burst on Cressie death (never soft-locks)
    const double StormcallInterval = 3.5;     // G: cadence between enrage stormcall strikes
    const double StormcallTelegraph = 1.0;    // G: delay between stormcall cast and ground hit
    const double StormcallRadius = 18.0;      // G: hit radius of GSEelBossStormcallLightningBolt
    const int MaxPendingStormcalls = 3;       // G: cap concurrent pending strikes
    const int BarrierHalfWidth = 14;          // G: server walk-barrier half-width along arenaBarrier.ao
    const int BarrierHalfThickness = 2;       // G: server walk-barrier half-thickness

    public enum ArenaPhase { Waiting, Intro, Phase1, Dismounting, Phase2, Enrage, Defeated }

    sealed class PlacedObj
    {
        public required uint Id { get; init; }
        public required string Type { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required float Rotation { get; init; }
        public float? Height { get; init; }
        public string? Model { get; init; }
    }

    sealed class BarrierState
    {
        public required DeepwaterBoard.Cell Cell { get; init; }
        public required (int X, int Y) TriggerAt { get; init; }
        public required PlacedObj Barrier { get; init; }
        public required PlacedObj PreFight { get; init; }
        public volatile bool Seen;
    }

    sealed class ArenaState
    {
        public required DeepwaterBoard.Cell Cell { get; init; }
        public required PlacedObj Barrier { get; init; }
        public required PlacedObj Teleporter { get; init; }
        public required PlacedObj InvisibleLantern { get; init; }
        public required PlacedObj PreFight { get; init; }
        public required PlacedObj Altar { get; init; }
        public required PlacedObj RightPool { get; init; }
        public required PlacedObj LeftPool { get; init; }
        public required (int X, int Y) Centre { get; init; }

        public volatile ArenaPhase Phase = ArenaPhase.Waiting;
        public bool PreFightRemoved;
        public bool AltarEnabled;
        public uint CressieId;
        public uint VelkaId;
        public bool CressieDead;
        public bool VelkaDead;
        public int CressiePool = 2; // 2 = Right, 1 = Left
        public double IntroEndsAt;
        public double DismountEndsAt;
        public double NextPoolSwapAt;
        public double EnrageGraceEndsAt;
        public bool EnrageGraceCleared;
        public double NextStormcallAt;
        public (int X, int Y) LastKillAt;
        public readonly List<(double HitAt, int X, int Y)> PendingStormcalls = [];

        public bool BarrierClosed => Phase is ArenaPhase.Intro or ArenaPhase.Phase1 or ArenaPhase.Dismounting
            or ArenaPhase.Phase2 or ArenaPhase.Enrage;

        public bool Contains((int X, int Y) at)
        {
            int radius = Math.Max(DeepwaterConstants.BaseLanternRadius, ArenaSafeMinRadius);
            return Near(at, (InvisibleLantern.X, InvisibleLantern.Y), radius)
                || (Phase != ArenaPhase.Waiting && Cell.Contains(at));
        }
    }

    sealed class EncounterState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public BarrierState? Barrier;
        public ArenaState? Arena;
    }

    static readonly ConditionalWeakTable<DeepwaterRun, EncounterState> Encounters = new();

    static Dictionary<string, StateMachineData?> States = [];
    static HashSet<uint> QuestDropHashes = [];

    public static void LoadGameData()
    {
        States = DeepwaterLoot.StatesOf(
            new HashSet<string>(StringComparer.Ordinal)
            {
                PreFightType, ArenaTeleporterType, InvisibleLanternType, DescensionObjectType,
                RightEelLocatorType, LeftEelLocatorType,
            });
        QuestDropHashes =
        [
            InstanceEntities.ObjectTypeHash(BossAllflameId),
            InstanceEntities.ObjectTypeHash(ForbiddenScriptureId),
            InstanceEntities.ObjectTypeHash(TsoathaDucatId),
        ];
    }

    static bool HasLeagueFlag(Player player, int flag) =>
        player.Character.LeagueQuestFlags.Contains(flag);

    static bool Near((int X, int Y) a, (int X, int Y) b, double distance)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= distance * distance;
    }

    static QuestObjectTypes.Info? Info(string type)
    {
        if (QuestObjectTypes.Find(type) is not { } info) return null;
        bool hasSm = States.GetValueOrDefault(type) is not null || info.Components.Contains("StateMachine");
        return QuestObject.CanSerialize(info.Components, hasSm) ? info : null;
    }

    static StateMachineData? BaseStates(string type) =>
        States.GetValueOrDefault(type) ?? (QuestObjectTypes.Find(type)?.Components.Contains("StateMachine") == true
            ? new StateMachineData([], 0) : null);

    // ---- eligibility & pure helpers (public for Part 3 & Gateway tests) ----

    /// <summary>Whether this player may enter the imbued Kishara's Rest boss arena (<c>#29 → #30</c>) in a Voyage at
    /// <paramref name="areaLevel"/>: requires level ≥ 83 and Vesper's barrier power (<c>LQF 1889</c> or <c>QF 3610</c>).</summary>
    public static bool CanEnterArena(Player player, int areaLevel) =>
        areaLevel >= MinArenaLevel
        && (HasLeagueFlag(player, VesperPowerGivenLeagueFlag) || ClientHasQuestFlag(player, VesperPowerGivenFlag));

    /// <summary>Rolls one guaranteed Velka unique name from <see cref="UniqueDropWeights"/> (ignores IIQ/IIR).</summary>
    public static string RollVelkaUniqueName(Random random)
    {
        int total = 0;
        for (int i = 0; i < UniqueDropWeights.Length; i++) total += UniqueDropWeights[i].Weight;
        int roll = random.Next(Math.Max(1, total));
        for (int i = 0; i < UniqueDropWeights.Length; i++)
        {
            if ((roll -= UniqueDropWeights[i].Weight) < 0) return UniqueDropWeights[i].Name;
        }
        return UniqueDropWeights[^1].Name;
    }

    /// <summary>Creates a Velka unique item by name, resolving <c>Stormblood Sapphire Flask</c> vs
    /// <c>Stormblood Topaz Flask</c> and applying <c>Pearl of Tsoatha</c>'s 10/14 1-support vs 4/14 2-support split
    /// (stripping <c>UniquePearlSupportTwo</c> on the 1-support roll).</summary>
    public static Item? CreateVelkaUnique(UniqueCatalog uniques, ItemCatalog items, string uniqueName, Random random,
        bool? twoSupportPearl = null)
    {
        if (uniques.Find(uniqueName) is not { } def) return null;
        if (uniques.Create(def, random) is not { } item) return null;
        if (def.Name.Equals("Pearl of Tsoatha", StringComparison.OrdinalIgnoreCase))
        {
            bool keepTwo = twoSupportPearl ?? random.Next(14) < 4;
            if (!keepTwo && items.Mods.ById(PearlSupportTwoModId) is { } secondSupport)
            {
                var filtered = item.Mods.Where(m => m.Hash != secondSupport.Hash).ToList();
                if (filtered.Count != item.Mods.Count) item = item with { Mods = filtered };
            }
        }
        return item;
    }

    /// <summary>Whether <paramref name="item"/> is one of Velka's non-Dredge first-kill quest drops (or any quest item
    /// that must not be marked Dredge on pickup).</summary>
    public static bool IsQuestDrop(ItemCatalog catalog, Item item) =>
        QuestDropHashes.Contains(item.TypeHash)
        || (catalog.ByHash(item.TypeHash) is { } bt && bt.Components.Contains("Quest"));

    /// <summary>Syncs Velka's quest-item flags (<c>QF 3607</c> for <c>Velka's Allflame</c>,
    /// <c>QF 3567</c> for <c>Forbidden Scripture</c>, <c>QF 3631</c> for <c>Ducat of Tsoatha's Gift</c>) from the
    /// player's carried inventories.</summary>
    public static async Task SyncVelkaQuestFlags(Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        uint allflameHash = InstanceEntities.ObjectTypeHash(BossAllflameId);
        uint scriptureHash = InstanceEntities.ObjectTypeHash(ForbiddenScriptureId);
        uint ducatHash = InstanceEntities.ObjectTypeHash(TsoathaDucatId);
        bool hasAllflame = false, hasScripture = false, hasDucat = false;
        foreach (var (_, entry) in QuestItems.Carried(ItemsOf(player)))
        {
            uint h = entry.Item.TypeHash;
            if (h == allflameHash) hasAllflame = true;
            else if (h == scriptureHash) hasScripture = true;
            else if (h == ducatHash) hasDucat = true;
        }

        List<string>? qfOn = null, qfOff = null;
        void WantQf(string flag, bool want)
        {
            bool has = player.Character.HasQuestFlag(flag);
            if (want && !has) (qfOn ??= []).Add(flag);
            else if (!want && has) (qfOff ??= []).Add(flag);
        }
        WantQf(HaveBossAllflameFlag, hasAllflame && !HasLeagueFlag(player, VelkaFlameGivenLeagueFlag));
        WantQf(HaveUpgradePiece5Flag, hasScripture && !HasLeagueFlag(player, UpgradePiece5TurnedInLeagueFlag));
        WantQf(HaveQuestDucat3Flag, hasDucat);

        if (qfOn is null && qfOff is null) return;
        if (qfOn is not null) await SetQuestFlags(player, qfOn, on: true);
        if (qfOff is not null) await SetQuestFlags(player, qfOff, on: false);
    }

    /// <summary>Sets <c>Velka's Allflame</c> pickup flag (<c>QF 3607</c>) when a player picks up
    /// <see cref="BossAllflameId"/>.</summary>
    public static async Task OnQuestItemPickup(IModuleContext module, Player player, Item item)
    {
        if (item.TypeHash == InstanceEntities.ObjectTypeHash(BossAllflameId))
        {
            if (!ClientHasQuestFlag(player, HaveBossAllflameFlag))
                await SetQuestFlags(player, [HaveBossAllflameFlag]);
            module.Log($"{CharacterName(player)}: picked up Velka's Allflame (QF 3607)");
        }
    }

    // ---- plan & spawn ----

    /// <summary>Plans Kishara's Rest (<c>#29</c> barrier or <c>#30</c> arena) once at <c>StartRun</c> when the run's
    /// board contains either room. Zero cost when absent.</summary>
    internal static void Plan(Player player, DeepwaterRun run, Action<string> log)
    {
        var barrierCell = run.Board.Cells.FirstOrDefault(c => c.Room == BarrierRoom);
        var arenaCell = run.Board.Cells.FirstOrDefault(c => c.Room == ArenaRoom);
        if (barrierCell is null && arenaCell is null) return;

        var roomObjects = player.Layout.Level?.RoomObjects ?? [];
        var enc = new EncounterState();

        if (barrierCell is not null)
        {
            var trigger = FindOrPlace(roomObjects, barrierCell, WaterWallTriggerType, 316, 251, 0f, null, null);
            var barrier = FindOrPlaceObj(player, roomObjects, barrierCell, BarrierType, 420, 359, -0.458992f, -96f, BarrierModel, forceModel: false);
            var preFight = FindOrPlaceObj(player, roomObjects, barrierCell, PreFightType, 477, 428, -1.61045f, 15.625f, PreFightModel, forceModel: false);
            enc.Barrier = new BarrierState
            {
                Cell = barrierCell,
                TriggerAt = (trigger.X, trigger.Y),
                Barrier = barrier,
                PreFight = preFight,
            };
            log($"Allflame Kishara's Rest (#29 barrier): trigger ({trigger.X}, {trigger.Y}), barrier 0x{barrier.Id:x} at ({barrier.X}, {barrier.Y}), VelkaPreFight 0x{preFight.Id:x} at ({preFight.X}, {preFight.Y})");
        }

        if (arenaCell is not null)
        {
            var barrier = FindOrPlaceObj(player, roomObjects, arenaCell, FightBarrierType, 410, 363, -0.444627f, null, BarrierModel, forceModel: false);
            var teleporter = FindOrPlaceObj(player, roomObjects, arenaCell, ArenaTeleporterType, 411, 343, 2.79516f, null, TeleporterModel, forceModel: false);
            var lantern = FindOrPlaceObj(player, roomObjects, arenaCell, InvisibleLanternType, 446, 456, -0.395142f, null, InvisibleLanternModel, forceModel: false);
            var preFight = FindOrPlaceObj(player, roomObjects, arenaCell, PreFightType, 481, 433, -2.01426f, 11f, PreFightModel, forceModel: false);
            // CRITICAL: VelkaDescensionObject.ot defaults to SareshBloodlineAscendancy.ao; always pass the .arm override!
            var altar = FindOrPlaceObj(player, roomObjects, arenaCell, DescensionObjectType, 478, 527, -0.219186f, -12f, DescensionModel, forceModel: true);
            var rightPool = FindOrPlaceObj(player, roomObjects, arenaCell, RightEelLocatorType, 481, 433, 0f, 20f, null, forceModel: false);
            var leftPool = FindOrPlaceObj(player, roomObjects, arenaCell, LeftEelLocatorType, 407, 466, 0f, 20f, null, forceModel: false);
            var centre = FindOrPlace(roomObjects, arenaCell, CentreEelLocatorType, 449, 471, -0.404933f, null, null);
            enc.Arena = new ArenaState
            {
                Cell = arenaCell,
                Barrier = barrier,
                Teleporter = teleporter,
                InvisibleLantern = lantern,
                PreFight = preFight,
                Altar = altar,
                RightPool = rightPool,
                LeftPool = leftPool,
                Centre = (centre.X, centre.Y),
                LastKillAt = (centre.X, centre.Y),
            };
            log($"Allflame Kishara's Rest (#30 arena): barrier 0x{barrier.Id:x} at ({barrier.X}, {barrier.Y}), teleporter 0x{teleporter.Id:x} at ({teleporter.X}, {teleporter.Y}), lantern ({lantern.X}, {lantern.Y}), centre ({centre.X}, {centre.Y}), altar 0x{altar.Id:x} at ({altar.X}, {altar.Y})");
        }

        Encounters.AddOrUpdate(run, enc);
    }

    static (int X, int Y, float Rotation, float? Height, string? Model) FindOrPlace(
        IReadOnlyList<PlacedTileObject> roomObjects, DeepwaterBoard.Cell cell, string type,
        float fallbackX, float fallbackY, float fallbackRot, float? fallbackZ, string? fallbackModel)
    {
        for (int i = 0; i < roomObjects.Count; i++)
        {
            var o = roomObjects[i];
            if (o.Type == type && cell.Contains((o.X, o.Y)))
            {
                float? z = float.IsNaN(o.Z) ? fallbackZ : o.Z;
                string? model = string.IsNullOrEmpty(o.Model) ? fallbackModel : o.Model;
                return (o.X, o.Y, o.Rotation, z, model);
            }
        }
        var (px, py, angle) = AreaObjects.Place(cell.Placed, fallbackX, fallbackY, fallbackRot);
        return (px, py, angle, fallbackZ, fallbackModel);
    }

    static PlacedObj FindOrPlaceObj(Player player, IReadOnlyList<PlacedTileObject> roomObjects, DeepwaterBoard.Cell cell,
        string type, float fallbackX, float fallbackY, float fallbackRot, float? fallbackZ, string? fallbackModel, bool forceModel)
    {
        var (x, y, rot, z, model) = FindOrPlace(roomObjects, cell, type, fallbackX, fallbackY, fallbackRot, fallbackZ, fallbackModel);
        var info = QuestObjectTypes.Find(type);
        string? effectiveModel = forceModel
            ? (string.IsNullOrEmpty(model) ? fallbackModel : model)
            : (info is { OwnModel: true } && (string.IsNullOrEmpty(model) || model == info.AnimatedModel) ? null : model);
        return new PlacedObj
        {
            Id = player.TakeObjectId(),
            Type = type,
            X = x,
            Y = y,
            Rotation = rot,
            Height = z,
            Model = effectiveModel,
        };
    }

    /// <summary>Creates Kishara's Rest objects on <paramref name="player"/> at entry/re-entry in their current state.</summary>
    internal static async Task Show(Player player, DeepwaterRun run)
    {
        if (!Encounters.TryGetValue(run, out var enc)) return;
        await enc.Gate.WaitAsync();
        try
        {
            if (enc.Barrier is { } b)
            {
                await CreateObject(player, b.Barrier, transitionState: 1, blocked: true, states: null, hideMinimap: false);
                SetBarrierCells(player, b.Barrier, blocked: true);
                var pfStates = BaseStates(PreFightType)?.Set("active", b.Seen ? 1 : 0).Set("lantern", 1);
                await CreateObject(player, b.PreFight, transitionState: 1, blocked: false, states: pfStates, hideMinimap: true);
            }

            if (enc.Arena is { } a)
            {
                bool closed = a.BarrierClosed;
                await CreateObject(player, a.Barrier, transitionState: (byte)(closed ? 2 : 1), blocked: closed, states: null, hideMinimap: false);
                SetBarrierCells(player, a.Barrier, blocked: closed);

                var tpStates = BaseStates(ArenaTeleporterType)?.Set("active", closed ? 1 : 0);
                await CreateObject(player, a.Teleporter, transitionState: 1, blocked: false, states: tpStates, hideMinimap: !closed);

                var lanternStates = BaseStates(InvisibleLanternType);
                await CreateObject(player, a.InvisibleLantern, transitionState: 1, blocked: false, states: lanternStates, hideMinimap: true);

                var rightStates = BaseStates(RightEelLocatorType)?.Set("splash", closed && !a.CressieDead && a.CressiePool == 2 ? 1 : 0);
                await CreateObject(player, a.RightPool, transitionState: 1, blocked: false, states: rightStates, hideMinimap: true);

                var leftStates = BaseStates(LeftEelLocatorType)?.Set("splash", closed && !a.CressieDead && a.CressiePool == 1 ? 1 : 0);
                await CreateObject(player, a.LeftPool, transitionState: 1, blocked: false, states: leftStates, hideMinimap: true);

                if (!a.PreFightRemoved)
                {
                    int pfActive = a.Phase == ArenaPhase.Intro ? 2 : 0;
                    int pfLantern = a.Phase == ArenaPhase.Intro ? 2 : 1;
                    var pfStates = BaseStates(PreFightType)?.Set("active", pfActive).Set("lantern", pfLantern);
                    await CreateObject(player, a.PreFight, transitionState: 1, blocked: false, states: pfStates, hideMinimap: true);
                }

                var altarStates = BaseStates(DescensionObjectType)
                    ?.Set("enabled", a.AltarEnabled ? 1 : 0)
                    .Set("visible", a.AltarEnabled ? 1 : 0);
                await CreateObject(player, a.Altar, transitionState: 1, blocked: false, states: altarStates, hideMinimap: !a.AltarEnabled);
                if (a.AltarEnabled && Info(DescensionObjectType) is { Blocking: true } altarInfo)
                    player.SetBlocker(a.Altar.Id, a.Altar.X, a.Altar.Y, Math.Max(1, altarInfo.Size));
            }
        }
        finally { enc.Gate.Release(); }
    }

    static async Task CreateObject(Player player, PlacedObj o, byte transitionState, bool blocked, StateMachineData? states, bool hideMinimap)
    {
        if (Info(o.Type) is not { } info) return;
        var obj = new QuestObject(o.X, o.Y, info.Components, o.Rotation, o.Height, o.Model,
            TransitionState: transitionState, Blocked: blocked)
        {
            StateMachine = states,
            HideMinimapIcon = hideMinimap,
        };
        var modPlayer = PlayerOf(player);
        if (!await global::Game.SendWorldCreate(modPlayer, o.Id,
            InstanceEntities.CreateObject(o.Id, InstanceEntities.ObjectTypeHash(o.Type), obj.Blob()),
            $"0x02bb Allflame Velka {o.Type.Split('/')[^1]} 0x{o.Id:x} at ({o.X}, {o.Y})"))
            return;
        await global::Game.SendWorldRecord(modPlayer, o.Id, InstanceProtocol.EnterWakeZone(o.Id));
    }

    static async Task SendObjectStates(Player player, PlacedObj o, StateMachineData states, string what)
    {
        if (Info(o.Type) is not { } info) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        await global::Game.SendWorldRecord(PlayerOf(player), o.Id, InstanceEntities.StateMachineUpdate(o.Id, (byte)index, states),
            $"0x02bc Allflame Velka {o.Type.Split('/')[^1]} 0x{o.Id:x}: {what}");
    }

    static async Task SetFightBarrier(Player player, PlacedObj barrier, bool closed)
    {
        if (Info(barrier.Type) is not { } info) return;
        int tIdx = QuestObject.TransitionableIndex(info.Components);
        byte state = (byte)(closed ? 2 : 1);
        foreach (var member in DeepwaterLoot.Members(player))
        {
            SetBarrierCells(member, barrier, closed);
            if (tIdx >= 0)
                await global::Game.SendWorldRecord(PlayerOf(member), barrier.Id,
                    InstanceEntities.TransitionableUpdate(barrier.Id, (byte)tIdx, state),
                    $"0x02bc Allflame VelkaFlameBarrierFight 0x{barrier.Id:x} -> state {state}");
            await global::Game.SendWorldRecord(PlayerOf(member), barrier.Id,
                InstanceEntities.BlockageUpdate(barrier.Id, closed),
                $"0x01c6 Allflame VelkaFlameBarrierFight 0x{barrier.Id:x} blocked={closed}");
        }
    }

    static void SetBarrierCells(Player player, PlacedObj barrier, bool blocked)
    {
        if (!blocked)
        {
            if (player.DoorBarriers.Remove(barrier.Id)) player.UpdateBarrier();
            return;
        }
        if (player.Layout.Walkable is not { } walk) return;
        var cells = new HashSet<(int X, int Y)>();
        double cos = Math.Cos(barrier.Rotation), sin = Math.Sin(barrier.Rotation);
        for (int u = -BarrierHalfWidth; u <= BarrierHalfWidth; u++)
            for (int v = -BarrierHalfThickness; v <= BarrierHalfThickness; v++)
            {
                int cx = barrier.X + (int)Math.Round(u * cos - v * sin);
                int cy = barrier.Y + (int)Math.Round(u * sin + v * cos);
                if (walk(cx, cy)) cells.Add((cx, cy));
            }
        if (cells.Count > 0)
        {
            player.DoorBarriers[barrier.Id] = cells;
            player.UpdateBarrier();
        }
    }

    static async Task BossStates(Player player, uint id, params (string Name, int Value)[] changes)
    {
        if (!player.Area.Monsters.TryGetValue(id, out var boss) || boss.States is not { } current || boss.Variety is not { } variety) return;
        foreach (var (name, value) in changes) current = current.Set(name, value);
        var states = boss.States = current;
        if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
        int component = variety.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (component < 0) return;
        foreach (var member in DeepwaterLoot.Members(player))
        {
            if (!member.Area.Monsters.TryGetValue(id, out var m) || m.Asleep) continue;
            await global::Game.SendWorldRecord(member, id,
                InstanceEntities.StateMachineUpdate(id, (byte)component, states), null);
        }
    }

    // ---- no-drown arena safe zone (hot path: O(1), 0 alloc) ----

    /// <summary>Whether <paramref name="at"/> is inside Kishara's Rest boss arena's <c>InvisibleLantern</c> light
    /// (called from <c>DeepwaterRun.InLight</c> so players never drown inside the boss arena). Zero allocations.</summary>
    internal static bool InArenaLight(DeepwaterRun run, (int X, int Y) at) =>
        Encounters.TryGetValue(run, out var enc) && enc.Arena is { } arena && arena.Contains(at);

    // ---- tick ----

    /// <summary>Per tick inside an Allflame run: handles Room <c>#29</c> water-wall discovery (<c>QF 3612</c> + taunt)
    /// and Room <c>#30</c> arena engage, Cressie emerge, 60% Phase 2 dismount, pool swaps, Cressie-death Stormcall
    /// enrage, and Victory. Zero allocations when the run has no Kishara's Rest cell or after completion.</summary>
    internal static async Task Tick(IModuleContext module, Player player, DeepwaterRun run)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent || run.Phase == DeepwaterRun.RunPhase.Ended) return;
        if (!Encounters.TryGetValue(run, out var enc)) return;

        bool needBarrier = enc.Barrier is { Seen: false };
        bool needArena = enc.Arena is { Phase: not ArenaPhase.Defeated };
        if (!needBarrier && !needArena) return;

        double now = AreaTime(player);
        var playerAt = player.Walk.Position(now);

        // Quick distance pre-filter before taking the semaphore (0 allocs on idle ticks).
        if (needBarrier && !needArena && (player.PlayerLife == 0 || !Near(playerAt, enc.Barrier!.TriggerAt, WaterWallTriggerRange)))
            return;
        if (!needBarrier && needArena && enc.Arena!.Phase == ArenaPhase.Waiting
            && (player.PlayerLife == 0 || !Near(playerAt, enc.Arena.Centre, ArenaEngageRange)))
            return;

        if (!enc.Gate.Wait(0)) return;
        try
        {
            if (enc.Barrier is { Seen: false } b && player.PlayerLife > 0 && Near(playerAt, b.TriggerAt, WaterWallTriggerRange))
                await TriggerWaterWall(module, player, b);

            if (enc.Arena is { Phase: not ArenaPhase.Defeated } a)
                await StepArena(module, player, run, a, playerAt, now);
        }
        finally { enc.Gate.Release(); }
    }

    static async Task TriggerWaterWall(IModuleContext module, Player player, BarrierState b)
    {
        b.Seen = true;
        var taunt = BaseStates(PreFightType)?.Set("active", 1).Set("lantern", 1);
        foreach (var member in DeepwaterLoot.Members(player))
        {
            if (!ClientHasQuestFlag(member, WaterWallSeenFlag))
                await SetQuestFlags(member, [WaterWallSeenFlag]);
            if (taunt is not null)
                await SendObjectStates(member, b.PreFight, taunt, "active=1 (Velka_Taunt)");
        }
        module.Log($"{CharacterName(player)}: saw Velka's water wall in Kishara's Rest (#29); set {WaterWallSeenFlag} (QF 3612)");
    }

    static async Task StepArena(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, (int X, int Y) playerAt, double now)
    {
        switch (a.Phase)
        {
            case ArenaPhase.Waiting:
                if (player.PlayerLife == 0 || !Near(playerAt, a.Centre, ArenaEngageRange)) return;
                await BeginArenaIntro(module, player, a, now);
                return;

            case ArenaPhase.Intro:
                if (now < a.IntroEndsAt) return;
                await StartPhase1(module, player, run, a, now);
                return;

            case ArenaPhase.Phase1:
                await StepPhase1(module, player, run, a, now);
                return;

            case ArenaPhase.Dismounting:
                await StepDismounting(module, player, run, a, now);
                return;

            case ArenaPhase.Phase2:
                await StepPhase2(module, player, run, a, now);
                return;

            case ArenaPhase.Enrage:
                await StepEnrage(module, player, run, a, now);
                return;
        }
    }

    static async Task BeginArenaIntro(IModuleContext module, Player player, ArenaState a, double now)
    {
        a.Phase = ArenaPhase.Intro;
        a.IntroEndsAt = now + IntroSeconds;
        await SetFightBarrier(player, a.Barrier, closed: true);

        var tpOn = BaseStates(ArenaTeleporterType)?.Set("active", 1);
        var pfIntro = BaseStates(PreFightType)?.Set("active", 2).Set("lantern", 2);
        var rightSplash = BaseStates(RightEelLocatorType)?.Set("splash", 1);
        foreach (var member in DeepwaterLoot.Members(player))
        {
            if (!ClientHasQuestFlag(member, VelkaSeenFlag))
                await SetQuestFlags(member, [VelkaSeenFlag]);
            if (tpOn is not null) await SendObjectStates(member, a.Teleporter, tpOn, "active=1");
            if (pfIntro is not null) await SendObjectStates(member, a.PreFight, pfIntro, "active=2, lantern=2 (emerge)");
            if (rightSplash is not null) await SendObjectStates(member, a.RightPool, rightSplash, "splash=1");
        }
        module.Log($"{CharacterName(player)}: entered Kishara's Rest boss arena (#30); barrier sealed, ArenaTeleporter active, {VelkaSeenFlag} set");
    }

    static async Task StartPhase1(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, double now)
    {
        if (!a.PreFightRemoved)
        {
            a.PreFightRemoved = true;
            foreach (var member in DeepwaterLoot.Members(player))
                await global::Game.SendWorldRemoval(PlayerOf(member), a.PreFight.Id, $"0x02bd remove VelkaPreFight 0x{a.PreFight.Id:x}");
        }

        if (Monsters.Find(CressieRow.ToString()) is not { Supported: true } cressieVariety)
        {
            module.Log($"{CharacterName(player)}: {CressieVariety} (#{CressieRow}) is not supported; aborting arena seal");
            a.Phase = ArenaPhase.Defeated;
            await SetFightBarrier(player, a.Barrier, closed: false);
            return;
        }

        var bossMods = (run.CellMods is { } cm && a.Cell.Index < cm.Length ? cm[a.Cell.Index] : null)
            ?? run.RunMods ?? DeepwaterMonsters.ModsFor(run.Level, null);
        uint id = await SpawnMonster(player, cressieVariety, (byte)Math.Clamp(run.Level, 1, 100),
            MonsterRarity.Unique, a.RightPool.X, a.RightPool.Y, log: true, alone: false, unrisen: false,
            rotation: a.PreFight.Rotation, extraMods: bossMods,
            owned: (_, m) => m.Aggro = true, placement: MonsterPlacement.Established, instanceOwned: true);
        if (id == 0) return;

        if (player.Area.Monsters.TryGetValue(id, out var boss))
        {
            boss.LifePhases = new([DismountThresholdPercent]);
            boss.Aggro = true;
        }
        a.CressieId = id;
        a.CressiePool = 2;
        a.Phase = ArenaPhase.Phase1;
        a.NextPoolSwapAt = now + PoolSwapInterval;
        await BossStates(player, id, ("boss_life_bar", 1), ("rider", 1), ("pool", 2), ("phase", 1), ("velkaalive", 1));
        module.Log($"{CharacterName(player)}: Phase 1 started — Cressie 0x{id:x} spawned at RightEelLocator ({a.RightPool.X}, {a.RightPool.Y}) with mounted Velka (rider=1)");
    }

    static async Task StepPhase1(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, double now)
    {
        bool dead, dismount = false;
        (int X, int Y) cressieAt = a.Centre;
        dead = !player.Area.Monsters.TryGetValue(a.CressieId, out var cressie) || cressie.Dead || cressie.Life == 0;
        if (!dead)
        {
            cressieAt = cressie!.Walk.Position(now);
            if (cressie.LifePhases is { Active: false } phases)
                dismount = phases.Begin(cressie.Life, cressie.MaxLife);
            else if ((ulong)cressie.Life * 100 <= (ulong)Math.Max(1u, cressie.MaxLife) * DismountThresholdPercent)
                dismount = true;
        }

        if (dead)
        {
            // Defensive: if Cressie somehow died before dismount (e.g., dev command), spawn Velka directly in Enrage so fight never soft-locks.
            a.CressieDead = true;
            a.LastKillAt = cressieAt;
            await SpawnVelka(module, player, run, a, enrageAtOnce: true, now);
            return;
        }

        if (dismount)
        {
            a.Phase = ArenaPhase.Dismounting;
            a.DismountEndsAt = now + DismountSeconds;
            await BossStates(player, a.CressieId, ("rider", 2), ("phase", 2));
            await SpawnVelka(module, player, run, a, enrageAtOnce: false, now);
            return;
        }

        if (now >= a.NextPoolSwapAt)
            await SwapCressiePool(module, player, a, now);
    }

    static async Task SpawnVelka(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, bool enrageAtOnce, double now)
    {
        if (a.VelkaId != 0) return;
        if (Monsters.Find(VelkaRow.ToString()) is not { Supported: true } velkaVariety)
        {
            module.Log($"{CharacterName(player)}: {VelkaVariety} (#{VelkaRow}) is not supported");
            a.VelkaDead = true;
            if (a.CressieDead) await CompleteVictory(module, player, run, a);
            return;
        }

        var spawnAt = player.Layout.Near(a.Centre);
        var bossMods = (run.CellMods is { } cm && a.Cell.Index < cm.Length ? cm[a.Cell.Index] : null)
            ?? run.RunMods ?? DeepwaterMonsters.ModsFor(run.Level, null);
        uint id = await SpawnMonster(player, velkaVariety, (byte)Math.Clamp(run.Level, 1, 100),
            MonsterRarity.Unique, spawnAt.X, spawnAt.Y, log: true, alone: false, unrisen: false,
            extraMods: bossMods,
            owned: (_, m) => m.Aggro = true, placement: MonsterPlacement.Proposed, instanceOwned: true);
        if (id == 0) return;

        a.VelkaId = id;
        await BossStates(player, id, ("boss_life_bar", 1));
        module.Log($"{CharacterName(player)}: Phase 2 dismount — Velka, the Tide Witch 0x{id:x} spawned at ({spawnAt.X}, {spawnAt.Y})");
        if (enrageAtOnce) await BeginEnrage(module, player, a, now);
    }

    static async Task StepDismounting(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, double now)
    {
        if (a.VelkaId == 0 && !a.VelkaDead) await SpawnVelka(module, player, run, a, enrageAtOnce: false, now);
        if (a.VelkaId == 0 && !a.VelkaDead) return;
        if (now < a.DismountEndsAt) return;
        if (player.Area.Monsters.TryGetValue(a.CressieId, out var cressie)) cressie.LifePhases?.Finish();
        a.Phase = ArenaPhase.Phase2;
        a.NextPoolSwapAt = now + PoolSwapInterval;
        await CheckDualBossState(module, player, run, a, now);
    }

    static async Task StepPhase2(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, double now)
    {
        if (await CheckDualBossState(module, player, run, a, now)) return;
        if (!a.CressieDead && now >= a.NextPoolSwapAt)
            await SwapCressiePool(module, player, a, now);
    }

    static async Task<bool> CheckDualBossState(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, double now)
    {
        bool cressieDead, velkaDead;
        (int X, int Y) cressieAt = a.LastKillAt, velkaAt = a.LastKillAt;
        cressieDead = a.CressieDead || a.CressieId == 0 || !player.Area.Monsters.TryGetValue(a.CressieId, out var c) || c.Dead || c.Life == 0;
        if (!cressieDead && player.Area.Monsters.TryGetValue(a.CressieId, out var cb)) cressieAt = cb.Walk.Position(now);
        velkaDead = a.VelkaDead || a.VelkaId == 0 || !player.Area.Monsters.TryGetValue(a.VelkaId, out var v) || v.Dead || v.Life == 0;
        if (!velkaDead && player.Area.Monsters.TryGetValue(a.VelkaId, out var vb)) velkaAt = vb.Walk.Position(now);

        if (cressieDead && !a.CressieDead)
        {
            a.CressieDead = true;
            a.LastKillAt = cressieAt;
            await ClearPoolSplashes(player, a);
        }
        if (velkaDead && !a.VelkaDead)
        {
            a.VelkaDead = true;
            a.LastKillAt = velkaAt;
            if (!a.CressieDead && a.CressieId != 0)
                await BossStates(player, a.CressieId, ("velkaalive", 0));
        }

        if (a.CressieDead && a.VelkaDead)
        {
            await CompleteVictory(module, player, run, a);
            return true;
        }
        if (a.CressieDead && !a.VelkaDead && a.Phase != ArenaPhase.Enrage)
        {
            await BeginEnrage(module, player, a, now);
            return true;
        }
        return false;
    }

    static async Task BeginEnrage(IModuleContext module, Player player, ArenaState a, double now)
    {
        a.Phase = ArenaPhase.Enrage;
        a.EnrageGraceEndsAt = now + EnrageGraceSeconds;
        a.EnrageGraceCleared = false;
        a.NextStormcallAt = now + 0.5;
        if (player.Area.Monsters.TryGetValue(a.VelkaId, out var velka)) velka.EntranceGrace = true;
        await BossStates(player, a.VelkaId, ("eel_death", 1), ("stormcall", 1));
        module.Log($"{CharacterName(player)}: Cressie defeated while Velka 0x{a.VelkaId:x} lives — Velka enters Stormcall enrage (eel_death=1, {EnrageGraceSeconds:0.0}s grace)");
    }

    static async Task StepEnrage(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, double now)
    {
        bool velkaDead;
        (int X, int Y) velkaAt = a.LastKillAt;
        velkaDead = a.VelkaDead || a.VelkaId == 0 || !player.Area.Monsters.TryGetValue(a.VelkaId, out var v) || v.Dead || v.Life == 0;
        if (!velkaDead && player.Area.Monsters.TryGetValue(a.VelkaId, out var vb))
        {
            velkaAt = vb.Walk.Position(now);
            if (!a.EnrageGraceCleared && now >= a.EnrageGraceEndsAt)
            {
                vb.EntranceGrace = false;
                a.EnrageGraceCleared = true;
            }
        }
        if (velkaDead)
        {
            a.VelkaDead = true;
            a.LastKillAt = velkaAt;
            a.PendingStormcalls.Clear();
            await CompleteVictory(module, player, run, a);
            return;
        }

        // Resolve any telegraphed Stormcall strikes that have reached impact time.
        if (a.PendingStormcalls.Count > 0)
        {
            for (int i = a.PendingStormcalls.Count - 1; i >= 0; i--)
            {
                var strike = a.PendingStormcalls[i];
                if (now < strike.HitAt) continue;
                a.PendingStormcalls.RemoveAt(i);
                await ResolveStormcallHit(player, run, a, (strike.X, strike.Y));
            }
        }

        // Schedule next Stormcall strike around an alive player in the arena.
        if (now >= a.NextStormcallAt && a.PendingStormcalls.Count < MaxPendingStormcalls)
        {
            a.NextStormcallAt = now + StormcallInterval;
            await CastStormcall(player, a, now);
        }
    }

    static async Task CastStormcall(Player player, ArenaState a, double now)
    {
        var members = DeepwaterLoot.Members(player).Where(m => m.PlayerLife > 0).ToList();
        if (members.Count == 0) return;
        var targetMember = members[AreaRng(player).Next(members.Count)];
        var targetAt = targetMember.Walk.Position(now);
        if (!a.Contains(targetAt)) return;

        ushort? skillId = null;
        (int X, int Y) from = a.Centre;
        if (!player.Area.Monsters.TryGetValue(a.VelkaId, out var velka) || velka.Dead) return;
        from = velka.Walk.Position(now);
        foreach (var entry in velka.Kit)
            if (entry.Effect == StormcallEffect) { skillId = entry.Attack.SkillId; break; }

        a.PendingStormcalls.Add((now + StormcallTelegraph, targetAt.X, targetAt.Y));
        if (skillId is ushort sid)
        {
            foreach (var member in members)
            {
                await global::Game.SendWorldRecord(member, a.VelkaId,
                    InstanceProtocol.ActionStartedNow(a.VelkaId, from.X, from.Y, targetAt.X, targetAt.Y,
                        sid, member.NextActionSequence(), 0x0800, 0), null);
            }
        }
    }

    static async Task ResolveStormcallHit(Player player, DeepwaterRun run, ArenaState a, (int X, int Y) at)
    {
        MonsterAttack? attack = null;
        if (!player.Area.Monsters.TryGetValue(a.VelkaId, out var velka) || velka.Dead) return;
        foreach (var entry in velka.Kit)
            if (entry.Effect == StormcallEffect) { attack = entry.Attack; break; }
        attack ??= velka.Attack;
        if (attack is null && velka.Variety is { } variety)
            attack = BossKits.AttackFor(player, variety, (byte)Math.Clamp(run.Level, 1, 100), StormcallEffect, 0x4000);
        if (attack is null || attack.Damage.IsZero) return;

        foreach (var member in DeepwaterLoot.Members(player))
        {
            if (member.PlayerLife == 0) continue;
            await ArenaHazards.Hit(member, a.VelkaId, velka, attack,
                pos => Near(pos, at, StormcallRadius),
                () => a.Phase == ArenaPhase.Enrage);
        }
    }

    static async Task SwapCressiePool(IModuleContext module, Player player, ArenaState a, double now)
    {
        a.NextPoolSwapAt = now + PoolSwapInterval;
        int nextPool = a.CressiePool == 2 ? 1 : 2;
        var dest = nextPool == 1 ? a.LeftPool : a.RightPool;
        if (!player.Area.Monsters.TryGetValue(a.CressieId, out var cressie) || cressie.Dead || cressie.Invulnerable) return;
        cressie.Walk.Teleport(dest.X, dest.Y, now);
        a.CressiePool = nextPool;
        var leftSplash = BaseStates(LeftEelLocatorType)?.Set("splash", nextPool == 1 ? 1 : 0);
        var rightSplash = BaseStates(RightEelLocatorType)?.Set("splash", nextPool == 2 ? 1 : 0);
        foreach (var member in DeepwaterLoot.Members(player))
        {
            await global::Game.SendWorldRecord(PlayerOf(member), a.CressieId, InstanceProtocol.Teleport(a.CressieId, dest.X, dest.Y),
                $"0x01b7 Cressie pool swap -> {(nextPool == 1 ? "Left" : "Right")} ({dest.X}, {dest.Y})");
            if (leftSplash is not null) await SendObjectStates(member, a.LeftPool, leftSplash, $"splash={(nextPool == 1 ? 1 : 0)}");
            if (rightSplash is not null) await SendObjectStates(member, a.RightPool, rightSplash, $"splash={(nextPool == 2 ? 1 : 0)}");
        }
        await BossStates(player, a.CressieId, ("pool", nextPool));
        module.Log($"{CharacterName(player)}: Cressie 0x{a.CressieId:x} swapped to pool {nextPool} ({dest.X}, {dest.Y})");
    }

    static async Task ClearPoolSplashes(Player player, ArenaState a)
    {
        var leftOff = BaseStates(LeftEelLocatorType)?.Set("splash", 0);
        var rightOff = BaseStates(RightEelLocatorType)?.Set("splash", 0);
        foreach (var member in DeepwaterLoot.Members(player))
        {
            if (leftOff is not null) await SendObjectStates(member, a.LeftPool, leftOff, "splash=0");
            if (rightOff is not null) await SendObjectStates(member, a.RightPool, rightOff, "splash=0");
        }
    }

    // ---- kill hook & victory ----

    /// <summary><c>GameEvents.MonsterKilled</c>: immediately handles Cressie / Velka death transitions and Victory.
    /// Short-circuits on non-Unique kills or runs without a Kishara's Rest arena (0 allocations).</summary>
    internal static async Task OnMonsterKilled(IModuleContext module, MonsterKill kill, DeepwaterRun run)
    {
        if (kill.Monster.Rarity != MonsterRarity.Unique) return;
        if (!Encounters.TryGetValue(run, out var enc) || enc.Arena is not { Phase: not ArenaPhase.Defeated } a) return;
        uint id = kill.Monster.ObjectId;
        if (id == 0 || (id != a.CressieId && id != a.VelkaId)) return;

        await enc.Gate.WaitAsync();
        try
        {
            if (a.Phase == ArenaPhase.Defeated) return;
            double now = AreaTime(kill.Player);
            if (id == a.CressieId)
            {
                a.CressieDead = true;
                a.LastKillAt = (kill.Monster.X, kill.Monster.Y);
                await ClearPoolSplashes(kill.Player, a);
                if (a.VelkaId == 0)
                {
                    await SpawnVelka(module, kill.Player, run, a, enrageAtOnce: true, now);
                    return;
                }
            }
            else if (id == a.VelkaId)
            {
                a.VelkaDead = true;
                a.LastKillAt = (kill.Monster.X, kill.Monster.Y);
                if (!a.CressieDead && a.CressieId != 0)
                    await BossStates(kill.Player, a.CressieId, ("velkaalive", 0));
            }

            if (a.CressieDead && a.VelkaDead)
                await CompleteVictory(module, kill.Player, run, a);
            else if (a.CressieDead && !a.VelkaDead && a.Phase != ArenaPhase.Enrage)
                await BeginEnrage(module, kill.Player, a, now);
        }
        finally { enc.Gate.Release(); }
    }

    static async Task CompleteVictory(IModuleContext module, Player player, DeepwaterRun run, ArenaState a)
    {
        if (a.Phase == ArenaPhase.Defeated) return;
        a.Phase = ArenaPhase.Defeated;
        a.AltarEnabled = true;
        a.PendingStormcalls.Clear();

        var members = DeepwaterLoot.Members(player).ToList();
        await SetFightBarrier(player, a.Barrier, closed: false);
        await ClearPoolSplashes(player, a);

        var tpOff = BaseStates(ArenaTeleporterType)?.Set("active", 0);
        var altarOn = BaseStates(DescensionObjectType)?.Set("enabled", 1).Set("visible", 1);
        int altarSize = Math.Max(1, Info(DescensionObjectType)?.Size ?? 3);

        foreach (var member in members)
        {
            if (tpOff is not null) await SendObjectStates(member, a.Teleporter, tpOff, "active=0");
            if (altarOn is not null) await SendObjectStates(member, a.Altar, altarOn, "enabled=1, visible=1 (Velka Bloodline)");
            member.SetBlocker(a.Altar.Id, a.Altar.X, a.Altar.Y, altarSize);

            await SetQuestFlags(member, [VelkaDefeatedJustNowFlag, VelkaDefeatedFlag]);
            if (!HasLeagueFlag(member, VelkaKilledLeagueFlag))
                await SetLeagueQuestFlags(member, [VelkaKilledLeagueFlag]);
        }

        await DropVictoryLoot(module, player, run, a, members);
        module.Log($"{CharacterName(player)}: Velka & Cressie defeated — barrier reopened, {VelkaDefeatedJustNowFlag}/{VelkaDefeatedFlag}/LQF {VelkaKilledLeagueFlag} set, VelkaDescensionObject 0x{a.Altar.Id:x} activated");
    }

    static async Task DropVictoryLoot(IModuleContext module, Player player, DeepwaterRun run, ArenaState a, List<Player> members)
    {
        var catalog = ItemsFor(player);
        var uniques = UniquesFor(player);
        var random = AreaRng(player);
        int level = Math.Clamp(run.Level, 1, 100);
        var dropAt = player.Layout.Near(a.LastKillAt);

        // 1. Guaranteed unique + independent 8% Coursing Current Support (ignores IIQ/IIR; marked Dredge on pickup).
        var dredgeDrops = new List<ItemDrops.Drop>(2);
        string uniqueName = RollVelkaUniqueName(random);
        if (CreateVelkaUnique(uniques, catalog, uniqueName, random) is { } uniqueItem
            && catalog.ByHash(uniqueItem.TypeHash) is { } uniqueBase)
        {
            dredgeDrops.Add(new(uniqueItem with { ItemLevel = (byte)Math.Clamp(level + 2, 1, 100), Identified = false }, uniqueBase));
        }
        else module.Log($"{CharacterName(player)}: Velka unique '{uniqueName}' could not be created");

        if (random.Next(100) < CoursingCurrentChancePercent
            && catalog.Find(CoursingCurrentSupportId) is { Supported: true } gemType)
        {
            dredgeDrops.Add(new(ItemCatalog.Create(gemType) with { ItemLevel = (byte)level }, gemType));
        }

        if (dredgeDrops.Count > 0)
            await DeepwaterLoot.Drop(player, dropAt.X, dropAt.Y, 4, "Velka, the Tide Witch", dredgeDrops);

        // 2. First-kill quest drops (private per eligible member, NOT Dredge: BossAllflameDeepwater, Forbidden Scripture,
        //    Ducat of Tsoatha's Gift).
        uint allflameHash = InstanceEntities.ObjectTypeHash(BossAllflameId);
        uint scriptureHash = InstanceEntities.ObjectTypeHash(ForbiddenScriptureId);
        uint ducatHash = InstanceEntities.ObjectTypeHash(TsoathaDucatId);
        var allflameType = catalog.Find(BossAllflameId) is { Supported: true } af ? af : null;
        var scriptureType = catalog.Find(ForbiddenScriptureId) is { Supported: true } sc ? sc : null;
        var ducatType = catalog.Find(TsoathaDucatId) is { Supported: true } du ? du : null;
        var allQuestNames = new List<string>();

        foreach (var member in members)
        {
            var memberDrops = new List<ItemDrops.Drop>(3);
            if (allflameType is not null
                && !HasLeagueFlag(member, VelkaFlameGivenLeagueFlag)
                && !ClientHasQuestFlag(member, HaveBossAllflameFlag)
                && !CarriesHash(member, allflameHash))
                memberDrops.Add(new(ItemCatalog.Create(allflameType) with { ItemLevel = (byte)level }, allflameType));

            if (scriptureType is not null
                && !HasLeagueFlag(member, UpgradePiece5TurnedInLeagueFlag)
                && !ClientHasQuestFlag(member, HaveUpgradePiece5Flag)
                && !CarriesHash(member, scriptureHash))
                memberDrops.Add(new(ItemCatalog.Create(scriptureType) with { ItemLevel = (byte)level }, scriptureType));

            if (ducatType is not null
                && !ClientHasQuestFlag(member, HaveQuestDucat3Flag)
                && !DeepwaterCursedDucats.Carries(member, ducatHash))
                memberDrops.Add(new(ItemCatalog.Create(ducatType) with { ItemLevel = (byte)level }, ducatType));

            if (memberDrops.Count == 0) continue;
            await DropPrivateQuestItems(member, dropAt.X, dropAt.Y, memberDrops);
            foreach (var d in memberDrops) allQuestNames.Add(d.Type.Name);
        }

        module.Log($"{CharacterName(player)}: Velka drops: {string.Join(", ", dredgeDrops.Select(d => d.Type.Name).Concat(allQuestNames))}");
    }

    static bool CarriesHash(Player player, uint typeHash) =>
        DeepwaterProgression.CarriesQuestItem(player, typeHash);

    static async Task DropPrivateQuestItems(Player member, int x, int y, IReadOnlyList<ItemDrops.Drop> drops)
    {
        if (!GameEvents.AllowsLoot(member)) return;
        for (int i = 0; i < drops.Count; i++)
        {
            double angle = i * 2.399963229728653 + 0.7;
            var at = member.Layout.Near((x + (int)Math.Round(Math.Cos(angle) * 5), y + (int)Math.Round(Math.Sin(angle) * 5)));
            var ground = new WorldItemObject(at.X, at.Y, drops[i].Item);
            // Quest items stay private to each eligible player (matching DeepwaterFairgraves.DropAllflame).
            uint id = member.Instance.Ground.Add(ground, member, shared: false);
            await SendGroundItem(member, id, ground,
                $"Velka quest drop {drops[i].Type.Name}", justDropped: true);
        }
    }

    // ---- object interaction (ArenaTeleporter & VelkaDescensionObject) ----

    /// <summary><c>C→S 0x24</c> on <c>ArenaTeleporter</c> (re-enter sealed arena) or <c>VelkaDescensionObject</c>
    /// (open Velka Bloodline <c>Descendancy#15 Brinerot</c> selector, matching <c>OshabiEncounter.cs</c> verbatim).
    /// Returns <c>false</c> for unrelated objects.</summary>
    internal static async Task<bool> Use(IModuleContext module, Player player, DeepwaterRun run,
        InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Encounters.TryGetValue(run, out var enc) || enc.Arena is not { } a) return false;
        bool isTeleporter = use.TargetId == a.Teleporter.Id;
        bool isAltar = !isTeleporter && use.TargetId == a.Altar.Id;
        if (!isTeleporter && !isAltar) return false;

        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || run.Failed)
        {
            await DeepwaterLoot.EndAction(player);
            return true;
        }

        Task End(int action) => global::Game.SendWorldRecord(player, player.Id,
            InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));

        if (isTeleporter)
        {
            if (!a.BarrierClosed) { await End(player.ActionCount); return true; }
            int tpReach = Math.Max(1, Info(ArenaTeleporterType)?.Size ?? 6) + 2;
            if (!TryBeginObjectWalk(player, requestedAt, a.Teleporter.X, a.Teleporter.Y, tpReach, out var tpWalk)) return true;
            var (action, now, here, stop, arrive) = tpWalk;
            if (!use.Started)
                await global::Game.SendWorldRecord(player, player.Id,
                    InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, a.Teleporter.X, a.Teleporter.Y,
                        use.SkillId, use.Sequence, use.Flags, targetObjectId: a.Teleporter.Id), null,
                    still: () => PlayerActionCurrent(player, action));
            if (arrive > now) await AreaDelay(player, arrive - now);
            try
            {
                if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || !a.BarrierClosed
                    || !PlayerWalk.InWalkUpRange(stop, (a.Teleporter.X, a.Teleporter.Y), tpReach)) return true;
                var to = player.Layout.Near(a.Centre);
                Interlocked.Increment(ref player.ActionCount);
                player.Walk.Teleport(to.X, to.Y, AreaTime(player));
                await global::Game.SendWorldRecord(PlayerOf(player), player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y),
                    $"0x01b7 Allflame ArenaTeleporter 0x{a.Teleporter.Id:x} -> ({to.X}, {to.Y})");
                module.Log($"{CharacterName(player)}: used ArenaTeleporter 0x{a.Teleporter.Id:x} -> ({to.X}, {to.Y})");
            }
            finally { await End(action); }
            return true;
        }

        // VelkaDescensionObject -> Velka Bloodline (Descendancy#15 Brinerot), matching OshabiEncounter.cs verbatim.
        if (!a.AltarEnabled) { await End(player.ActionCount); return true; }
        int altarReach = Math.Max(0, StationaryCollision.Resolve(DescensionObjectType).Size);
        if (!TryBeginObjectWalk(player, requestedAt, a.Altar.X, a.Altar.Y, altarReach, out var walk)) return true;
        {
            var (action, now, here, stop, arrive) = walk;
            if (!use.Started)
                await global::Game.SendWorldRecord(player, player.Id,
                    InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, a.Altar.X, a.Altar.Y,
                        use.SkillId, use.Sequence, use.Flags, targetObjectId: a.Altar.Id), null,
                    still: () => PlayerActionCurrent(player, action));
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (PlayerActionCurrent(player, action) && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
                && !run.Failed && a.AltarEnabled && PlayerWalk.InWalkUpRange(stop, (a.Altar.X, a.Altar.Y), altarReach))
            {
                byte row = Descendancies.Find(BloodlineId)?.Index ?? BloodlineRow;
                await OpenDescendancySelector(player, row);
            }
            await End(action);
            return true;
        }
    }

    // ---- diagnostics ----

    /// <summary>Kishara's Rest state for <c>/state</c>, or <c>null</c> when this run has no Velka room.</summary>
    internal static JsonObject? State(DeepwaterRun run)
    {
        if (!Encounters.TryGetValue(run, out var enc)) return null;
        var obj = new JsonObject();
        if (enc.Barrier is { } b)
        {
            obj["barrierRoom"] = new JsonObject
            {
                ["seen"] = b.Seen,
                ["triggerX"] = b.TriggerAt.X,
                ["triggerY"] = b.TriggerAt.Y,
                ["barrierId"] = b.Barrier.Id,
                ["preFightId"] = b.PreFight.Id,
            };
        }
        if (enc.Arena is { } a)
        {
            obj["arena"] = new JsonObject
            {
                ["phase"] = a.Phase.ToString(),
                ["barrierClosed"] = a.BarrierClosed,
                ["cressieId"] = a.CressieId,
                ["cressieDead"] = a.CressieDead,
                ["cressiePool"] = a.CressiePool,
                ["velkaId"] = a.VelkaId,
                ["velkaDead"] = a.VelkaDead,
                ["altarId"] = a.Altar.Id,
                ["altarEnabled"] = a.AltarEnabled,
                ["teleporterId"] = a.Teleporter.Id,
            };
        }
        return obj;
    }
}

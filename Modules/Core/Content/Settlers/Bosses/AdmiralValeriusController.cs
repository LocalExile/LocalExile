using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Settlers.Bosses;

/// <summary>
/// Admiral Valerius (<c>MonsterVarieties#10340</c>, <c>PirateCoveBoss</c>) in Sailor's Folly
/// (<c>WorldAreas#1920</c>, <c>SettlersBossPirateCove</c>, level 83, <c>piratecove/rooms/arena.arm</c>).
/// <para>Retail 3.29.3c encounter mechanics:</para>
/// <list type="bullet">
/// <item>Arena objects: <c>KalguurCannonballDaemon__</c> (#8795), <c>PirateCannon1</c>/<c>PirateCannon2</c> (NPC#683/684),
/// 9 outer cove <c>WaveMarker</c> spawn points, <c>BossForceFieldDoor</c> (seals on engage, opens on kill), and
/// <c>SettlersBossPortal</c> (<c>boss_aggro=2</c> during combat, <c>1</c> open to Kingsmarch before/after).</item>
/// <item>Full retail skill kit: Cold Melee (<c>MAASPirateColdMelee</c>), Double Strike (75% cold), Dash Thrust + cone of
/// <c>HeistAdmiralDashMortars</c> (100% cold), Dodge Back/Left/Right, Cannon Fire (<c>CannoballMarker</c> ->
/// <c>cannonball_once.ao</c> fire/phys bombardment via <c>KalguurCannonballDaemon__</c>), Anchor Smash
/// (<c>anchor_marker.ao</c> -> <c>anchor_slam.ao</c> -> <c>anchor_crumble.ao</c>, 50% cold), Water Geysers
/// (<c>PirateGeyser</c> applying <c>BuffDefinitions#2477</c> "Soaked to the Bone" for 20s, up to 8 stacks, +10% damage taken
/// per stack), and sweeping Tidal Waves (<c>KalguurAdmiralWave</c> #10376 / <c>KalguurAdmiralWaveGauntlet</c> #10377).</item>
/// <item>Phases: 100%–66% Phase 1; 66% invulnerable intermission (Geyser Maze + Tidal Waves); 66%–33% Phase 2;
/// 33% invulnerable intermission (Geyser Maze + Tidal Waves); 33%–0% Phase 3 (stops Anchor Smash, continuous sweeping Tidal Waves).</item>
/// <item>Post-kill: sets <c>SettlersKilledAdmiralValerius</c> + LeagueQuestFlag 1452 (unlocking Raulf's dialogue),
/// reopens barrier &amp; portal, invokes <see cref="SettlersBossCallbacks.OnRescueShipment"/>, and drops massive owner-only
/// Gold (50,000–65,000) + <c>Admiral's Arrogance</c> (unique chance) + 1–2 Runegrafts.</item>
/// </list>
/// </summary>
public static class AdmiralValeriusController
{
    public const string AreaId = "SettlersBossPirateCove";
    public const ushort AreaHash = 1660; // WorldAreas#1920
    public const int AreaLevel = 83;

    public const string BossVarietyId = "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/PirateCoveBoss"; // #10340
    public const string CannonballDaemonId = "Metadata/Monsters/LeagueKalguur/Boss/DarnawPirateRaider/KalguurCannonballDaemon__"; // #8795
    public const string WaveVarietyId = "Metadata/Monsters/LeagueKalguur/Boss/KalguurAdmiralWave"; // #10376
    public const string WaveGauntletVarietyId = "Metadata/Monsters/LeagueKalguur/Boss/KalguurAdmiralWaveGauntlet"; // #10377
    public const string WaveMarkerType = "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/WaveMarker";
    public const string PirateCannon1Type = "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/PirateCannon1";
    public const string PirateCannon2Type = "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/PirateCannon2";
    public const string CannonballMarkerType = "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/CannoballMarker";
    public const string PirateGeyserType = "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/PirateGeyser";

    public const string KilledQuestFlag = "SettlersKilledAdmiralValerius";
    public const int KilledLeagueQuestFlag = 1452; // Unlocks Raulf NPCTalk#3559 ("Admiral Valerius")

    // BuffDefinitions#2477 (kalguur_admiral_geyser_debuff, "Soaked to the Bone"): 20s, max 8 stacks, +10% damage taken per stack.
    public const ushort SoakedBuffRow = 2477;
    public const int SoakedMaxStacks = 8;
    public const int SoakedDamageTakenPerStack = 10;
    public const double SoakedDurationSeconds = 20.0;

    // Verified GrantedEffects rows from client.db (MonsterVarieties#10340, #8795, #10376, #10377).
    const int EffectColdMelee = 457;
    const int EffectDoubleStrike = 11573;
    const int EffectDoubleStrikeHit = 12485;
    const int EffectDashThrust = 12486;
    const int EffectDashThrustHit = 12487;
    const int EffectDashMortars = 12500;
    const int EffectDodgeBack = 12488;
    const int EffectDodgeLeft = 12489;
    const int EffectDodgeRight = 12490;
    const int EffectSummonCannonFire = 12491;
    const int EffectSummonGeysers = 12492;
    const int EffectGeyserDamage = 12502;
    const int EffectSummonWaves = 12505;
    const int EffectTidalWave = 12498;
    const int EffectTidalWaveDamage = 11596;
    const int EffectTidalWaveGauntletDamage = 11597;
    const int EffectCannonBallGT = 12506;
    const int EffectCannonBallEDS = 12507;
    const int EffectAnchorDropEAS = 12508;
    const int EffectAnchorDropGA = 12509;

    static readonly int[] BossEffects =
    [
        EffectColdMelee, EffectDoubleStrike, EffectDoubleStrikeHit,
        EffectDashThrust, EffectDashThrustHit, EffectDashMortars,
        EffectDodgeBack, EffectDodgeLeft, EffectDodgeRight,
        EffectSummonCannonFire, EffectSummonGeysers, EffectGeyserDamage,
        EffectSummonWaves, EffectTidalWave, EffectAnchorDropEAS, EffectAnchorDropGA,
    ];
    static readonly int[] DaemonEffects = [EffectCannonBallGT, EffectCannonBallEDS];
    static readonly int[] WaveEffects = [EffectTidalWaveDamage];
    static readonly int[] WaveGauntletEffects = [EffectTidalWaveGauntletDamage];

    // Verified .ao paths from data/game-files.zip.
    const string ArtRoot = "Metadata/Effects/Spells/monsters_effects/League_Settlers/PirateCoveBoss/ao/";
    const string ArtAnchorMarker = ArtRoot + "anchor_marker.ao";
    const string ArtAnchorSlam = ArtRoot + "anchor_slam.ao";
    const string ArtAnchorCrumble = ArtRoot + "anchor_crumble.ao";
    const string ArtCannonMarker = ArtRoot + "cannonball_marker.ao";
    const string ArtCannonImpact = ArtRoot + "cannonball_once.ao";
    const string ArtCannonMuzzle = "Metadata/Effects/Spells/monsters_effects/League_Heist/bosses/admiralDarnaw/cannonmuzzle.ao";
    const string ArtDashSlash = ArtRoot + "dash_slash.ao";
    const string ArtGeyserMarker = ArtRoot + "geyser_marker.ao";
    const string ArtGeyser = ArtRoot + "geyser.ao";
    const string ArtShield = ArtRoot + "shield.ao";
    const string ArtTeleport = ArtRoot + "teleport/rig.ao";
    const string ArtWave = ArtRoot + "wave.ao";

    // Verified projectile from Projectiles.dat#1030.
    static readonly SkillCatalog.MonsterProjectileInfo DashMortarShot =
        new("Metadata/Projectiles/HeistAdmiralDashMortars", 1500, Lobbed: true, 2)
        {
            Count = 5,
            SpreadRadians = 1.05,
            AreaRadius = 8,
        };

    // Retail voice lines (NPCTextAudio, NPCs#682).
    static readonly string[] LinesEmerge = ["AdmiralValerious_Emerge_Random", "AdmiralValerious_Emerge_2"];
    static readonly string[] LinesDoubleStrike = ["AdmiralValerious_DoubleStrike_Random", "AdmiralValerious_DoubleStrike_2"];
    static readonly string[] LinesDash = ["AdmiralValerious_Dash_Random", "AdmiralValerious_Dash_2"];
    static readonly string[] LinesAnchor = ["AdmiralValerious_Anchor_Random", "AdmiralValerious_Anchor_2", "AdmiralValerious_Anchor_3"];
    static readonly string[] LinesGeyser = ["AdmiralValerious_Geyser_Random", "AdmiralValerious_Geyser_2"];
    static readonly string[] LinesWave = ["AdmiralValerious_Wave_Random", "AdmiralValerious_Wave_2", "AdmiralValerious_Wave_3"];
    static readonly string[] LinesDeath = ["AdmiralValerious_Death_Random", "AdmiralValerious_Death_2", "AdmiralValerious_Death_3"];

    // Retail unique drop pool: Admiral's Arrogance (Antique Gauntlets).
    public static readonly (string Name, int Weight)[] UniqueDropPool = [("Admiral's Arrogance", 100)];
    public const double AdmiralsArroganceDropChance = 0.25;

    static readonly int[] PhaseThresholds = [66, 33];
    const string SaveKey = "Settlers/boss_admiral_valerius";

    // Exact fallback coordinates from piratecove/rooms/arena.arm.
    static readonly (int X, int Y) DefaultCentre = (339, 297);
    static readonly (int X, int Y) DefaultBossSpawn = (343, 301);
    const float DefaultBossRotation = 2.28519f;
    static readonly (int X, int Y) DefaultDaemonSpawn = (331, 299);
    static readonly (int X, int Y) DefaultDoorAt = (446, 382);
    const float DefaultDoorRot = 2.08005f;
    static readonly (int X, int Y) DefaultPortalAt = (606, 510);
    const float DefaultPortalRot = -0.94383f;

    static readonly (int X, int Y)[] DefaultWaveMarkers =
    [
        (306, 443), (393, 433), (493, 323), (464, 234), (397, 175),
        (289, 169), (217, 233), (185, 324), (228, 404),
    ];

    // Exact PirateCannon1/2 & broadside Cannon positions from piratecove/rooms/arena.arm.
    static readonly (int X, int Y)[] DefaultCannons =
    [
        (518, 617), (504, 621), (490, 630),
        (687, 382), (685, 365), (686, 349),
    ];

    sealed class PendingStrike
    {
        public required double HitAt { get; init; }
        public required (int X, int Y) Center { get; init; }
        public required double Radius { get; init; }
        public required MonsterAttack Attack { get; init; }
        public required string ImpactArt { get; init; }
        public string? AfterArt { get; init; }
        public uint MarkerObjId { get; init; }
        public string? MarkerObjType { get; init; }
        public bool AppliesSoaked { get; init; }
        public uint SourceId { get; init; }
    }

    sealed class ActiveWave
    {
        public required uint MonsterId { get; init; }
        public required bool Gauntlet { get; init; }
        public required (double X, double Y) Pos { get; set; }
        public required (double Dx, double Dy) Dir { get; init; }
        public required double Speed { get; init; }
        public required double EndsAt { get; init; }
        public double NextMoveAt;
        public readonly Dictionary<uint, double> PlayerHitCooldown = [];
    }

    sealed class ArenaState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public (int X, int Y) Centre = DefaultCentre;
        public (int X, int Y) DoorAt = DefaultDoorAt;
        public float DoorRot = DefaultDoorRot;
        public uint DoorId;
        public bool DoorClosed;

        public (int X, int Y) PortalAt = DefaultPortalAt;
        public float PortalRot = DefaultPortalRot;
        public uint PortalId;
        public bool PortalOpen = true;

        public readonly List<(int X, int Y)> WaveMarkers = [];
        public readonly List<(int X, int Y)> Cannons = [];
        public uint BossId;
        public uint DaemonId;
        public int Level = AreaLevel;
        public bool Engaged;
        public bool Defeated;
        public int Phase = 1; // 1 (100-66%), 2 (66-33%), 3 (33-0%)
        public bool InIntermission;
        public double IntermissionEndsAt;
        public double NextIntermissionPulseAt;
        public double NextAmbientWaveAt;
        public uint ShieldFxId;

        // Per-skill cooldowns.
        public double CdDoubleStrike;
        public double CdDashThrust;
        public double CdCannonFire;
        public double CdAnchorSmash;
        public double CdGeyser;
        public double CdDodge;

        public readonly List<PendingStrike> PendingStrikes = [];
        public readonly List<ActiveWave> ActiveWaves = [];
        public readonly List<SettlersBosses.TimedEffect> Effects = [];
        public readonly HashSet<Player> PresentedPlayers = [];
    }

    static readonly ConditionalWeakTable<InstanceState, ArenaState> Arenas = new();

    internal static void ResetOnUnload() => Arenas.Clear();

    static ArenaState GetOrCreateArena(Player player) => Arenas.GetValue(player.Instance, inst =>
    {
        var state = new ArenaState
        {
            Level = MapRunOf(player) is { Area.Level: > 0 } run ? run.Area.Level : AreaLevel,
            DoorId = inst.World.NewMonsterId(),
            PortalId = inst.World.NewMonsterId(),
        };
        if (inst.ContentState.TryGetValue(SaveKey, out string? saved) && saved == "defeated")
            state.Defeated = true;

        var roomObjects = player.Layout.Level?.RoomObjects ?? [];
        if (roomObjects.FirstOrDefault(o => o.Type == SettlersBosses.ArenaCentreMarkerType) is { } ctr)
            state.Centre = (ctr.X, ctr.Y);
        if (roomObjects.FirstOrDefault(o => o.Type == SettlersBosses.BossForceFieldDoorType) is { } door)
        {
            state.DoorAt = (door.X, door.Y);
            state.DoorRot = door.Rotation;
        }
        if (roomObjects.FirstOrDefault(o => o.Type == SettlersBosses.SettlersBossPortalType) is { } portal)
        {
            state.PortalAt = (portal.X, portal.Y);
            state.PortalRot = portal.Rotation;
        }

        var markers = roomObjects.Where(o => o.Type == WaveMarkerType).Select(o => (o.X, o.Y)).ToList();
        state.WaveMarkers.AddRange(markers.Count > 0 ? markers : DefaultWaveMarkers);
        var cannons = roomObjects
            .Where(o => o.Type is PirateCannon1Type or PirateCannon2Type or "Metadata/Monsters/LeagueKalguur/DarnawPirateRaider/Cannon")
            .Select(o => (o.X, o.Y)).ToList();
        state.Cannons.AddRange(cannons.Count > 0 ? cannons : DefaultCannons);
        return state;
    });

    internal static async Task OnAreaGenerated(Player player)
    {
        var arena = GetOrCreateArena(player);
        await PresentArenaObjects(player, arena);
    }

    internal static async Task OnAreaPopulated(Player player, byte level)
    {
        var arena = GetOrCreateArena(player);
        await arena.Gate.WaitAsync();
        try
        {
            arena.Level = Math.Max(AreaLevel, (int)level);
            if (arena.Defeated)
            {
                foreach (var (id, m) in player.Area.Monsters.ToArray())
                    if (m.Variety?.Id is BossVarietyId or CannonballDaemonId or WaveVarietyId or WaveGauntletVarietyId)
                        await SettlersBosses.DespawnMonster(player, id);
                return;
            }

            // Replace auto-populated boss with scripted PirateCoveBoss.
            var spawned = await SettlersBosses.EnsureScriptedBoss(
                player, BossVarietyId, (byte)Math.Clamp(arena.Level, 1, 100),
                DefaultBossSpawn, DefaultBossRotation, BossEffects);
            if (spawned is { } b)
            {
                arena.BossId = b.Id;
                b.Monster.LifePhases = new LifePhaseGate(PhaseThresholds);
            }

            // Replace or spawn KalguurCannonballDaemon__ (8795) as scripted helper.
            var daemonSpawned = await SettlersBosses.EnsureScriptedBoss(
                player, CannonballDaemonId, (byte)Math.Clamp(arena.Level, 1, 100),
                DefaultDaemonSpawn, 0f, DaemonEffects);
            if (daemonSpawned is { } d)
            {
                arena.DaemonId = d.Id;
                d.Monster.DamageImmune = true;
                d.Monster.NoRewards = true;
                d.Monster.NonBlocking = true;
            }
        }
        finally { arena.Gate.Release(); }
    }

    internal static async Task OnPlayerEntered(Player player)
    {
        var arena = GetOrCreateArena(player);
        await PresentArenaObjects(player, arena);

        await arena.Gate.WaitAsync();
        try
        {
            if (!arena.Defeated && arena.BossId == 0)
            {
                foreach (var (id, m) in player.Area.Monsters.ToArray())
                    if (m.Variety?.Id == BossVarietyId && SettlersBosses.Alive(m)) { arena.BossId = id; break; }
            }
            if (!arena.Defeated && arena.BossId != 0 && player.Area.Monsters.TryGetValue(arena.BossId, out var boss))
            {
                boss.LifePhases ??= new LifePhaseGate(PhaseThresholds);
                if (arena.Engaged) await SettlersBosses.EnsureBossBar(player, arena.BossId, boss);
            }
        }
        finally { arena.Gate.Release(); }
    }

    static async Task PresentArenaObjects(Player player, ArenaState arena)
    {
        await arena.Gate.WaitAsync();
        try
        {
            if (!arena.PresentedPlayers.Add(player)) return;
            await SettlersBosses.CreateQuestObj(player, arena.DoorId, SettlersBosses.BossForceFieldDoorType,
                arena.DoorAt.X, arena.DoorAt.Y, arena.DoorRot,
                transitionState: (byte)(arena.DoorClosed ? 2 : 1), blocked: arena.DoorClosed, hideMinimap: true);
            SettlersBosses.SetLineBarrier(player, arena.DoorId, arena.DoorAt.X, arena.DoorAt.Y, arena.DoorRot, 14, 2, arena.DoorClosed);

            await SettlersBosses.CreateBossPortal(player, arena.PortalId, arena.PortalAt.X, arena.PortalAt.Y, arena.PortalRot, arena.PortalOpen);
        }
        finally { arena.Gate.Release(); }
    }

    static async Task SetArenaSealed(Player runner, ArenaState arena, bool sealedState)
    {
        arena.DoorClosed = sealedState;
        arena.PortalOpen = !sealedState;
        await SettlersBosses.UpdateObjTransition(runner, arena.DoorId, SettlersBosses.BossForceFieldDoorType,
            (byte)(sealedState ? 2 : 1), blocked: sealedState);
        foreach (var member in SettlersBosses.ActivePlayers(runner))
            SettlersBosses.SetLineBarrier(member, arena.DoorId, arena.DoorAt.X, arena.DoorAt.Y, arena.DoorRot, 14, 2, sealedState);

        var portalSm = SettlersBosses.BaseStates(SettlersBosses.SettlersBossPortalType)
            ?.Set("boss_aggro", arena.PortalOpen ? 1 : 2);
        if (portalSm is not null)
            await SettlersBosses.UpdateObjStates(runner, arena.PortalId, SettlersBosses.SettlersBossPortalType, portalSm);
    }

    internal static async Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        if (!Arenas.TryGetValue(player.Instance, out var arena)) return;
        if (arena.Defeated && arena.Effects.Count == 0 && arena.ActiveWaves.Count == 0) return;

        if (!arena.Gate.Wait(0)) return;
        try
        {
            double now = AreaTime(player);
            await SettlersBosses.ExpireEffects(player, arena.Effects, now);
            await TickSoakedExpiries(player, now);

            if (arena.BossId == 0 || !player.Area.Monsters.TryGetValue(arena.BossId, out var boss) || !SettlersBosses.Alive(boss))
            {
                if (!arena.Defeated && arena.BossId != 0)
                    await CompleteVictory(player, arena, arena.Centre);
                return;
            }

            await StepPendingStrikes(player, arena, boss, now);
            await StepActiveWaves(player, arena, boss, now);

            // Let active intermissions complete naturally on timer (never skip a phase on death or entry grace period).
            if (arena.InIntermission && now >= arena.IntermissionEndsAt)
            {
                await EndIntermission(player, arena, boss);
                return;
            }

            if (!player.AiEnabled) return;

            var target = SettlersBosses.NearestLivingPlayer(player, boss.Walk.Position(now), now, maxRange: 190, awakeId: arena.BossId);
            if (target is null) return;

            // Engage when player crosses into the cove arena.
            if (!arena.Engaged)
            {
                if (!SettlersBosses.Near(target.Walk.Position(now), arena.Centre, 110)) return;
                arena.Engaged = true;
                boss.Aggro = true;
                await SetArenaSealed(player, arena, sealedState: true);
                await SettlersBosses.SetBossStates(player, arena.BossId, ("boss_life_bar", 1), ("sword_drawn", 1), ("global_first_aggro", 1));
                await SettlersBosses.EnsureBossBar(player, arena.BossId, boss);
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesEmerge);
                boss.BusyUntil = now + 1.4;
                return;
            }

            // Check 66% and 33% life intermissions.
            if (!arena.InIntermission
                && boss.LifePhases is { Active: false } phases
                && phases.Begin(boss.Life, boss.MaxLife))
            {
                await StartIntermission(player, arena, boss, now);
                return;
            }

            if (arena.InIntermission)
            {
                await StepIntermission(player, arena, boss, target, now);
                return;
            }

            // Phase 3 (<33% HP): continuous sweeping Tidal Waves persist for the remainder of the fight!
            if (arena.Phase >= 3 && now >= arena.NextAmbientWaveAt)
            {
                arena.NextAmbientWaveAt = now + 4.8;
                await SpawnTidalWaves(player, arena, count: 2, now, gauntlet: true);
            }

            if (now < boss.BusyUntil) return;
            await ActCombat(player, arena, boss, target, now);
        }
        finally { arena.Gate.Release(); }
    }

    static async Task StartIntermission(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        arena.InIntermission = true;
        arena.IntermissionEndsAt = now + 9.0;
        arena.NextIntermissionPulseAt = now + 1.0;

        SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, true);
        await SettlersBosses.SpawnTimedEffect(player, arena.Effects, boss.Walk.Position(now), ArtTeleport, now + 1.2);
        await SettlersBosses.TeleportBoss(player, arena.BossId, boss, arena.Centre, now);
        arena.ShieldFxId = await SettlersBosses.SpawnTimedEffect(player, arena.Effects, arena.Centre, ArtShield, now + 9.2);
        await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectSummonGeysers, arena.Centre, now, 3.0);
        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesGeyser);

        // Immediately spawn the first ring of Water Geysers + Tidal Waves.
        await SpawnGeyserMaze(player, arena, boss, now, count: 10);
        await SpawnTidalWaves(player, arena, count: 3, now, gauntlet: true);
    }

    static async Task StepIntermission(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        if (now >= arena.IntermissionEndsAt)
        {
            await EndIntermission(player, arena, boss);
            return;
        }
        if (now < arena.NextIntermissionPulseAt) return;
        arena.NextIntermissionPulseAt = now + 2.4;

        await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectSummonWaves, target.Walk.Position(now), now, 1.5);
        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesWave);
        await SpawnGeyserMaze(player, arena, boss, now, count: 8);
        await SpawnTidalWaves(player, arena, count: 3, now, gauntlet: true);
    }

    static async Task EndIntermission(Player player, ArenaState arena, SpawnedMonster boss)
    {
        arena.InIntermission = false;
        arena.Phase = Math.Min(3, arena.Phase + 1);
        SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, false);
        boss.LifePhases?.Finish();
        if (arena.ShieldFxId != 0)
        {
            uint sfx = arena.ShieldFxId;
            arena.ShieldFxId = 0;
            arena.Effects.RemoveAll(e => e.Id == sfx);
            foreach (var member in SettlersBosses.ActivePlayers(player))
                await SendWorldRemoval(PlayerOf(member), sfx, null);
        }
        if (arena.Phase >= 3)
            arena.NextAmbientWaveAt = AreaTime(player) + 2.5;
    }

    static async Task ActCombat(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        var bPos = boss.Walk.Position(now);
        var tPos = target.Walk.Position(now);
        double dist = SettlersBosses.Distance(bPos, tPos);
        double facing = Math.Atan2(tPos.Y - bPos.Y, tPos.X - bPos.X);

        // 1. Anchor Smash ("Sink!" / "Depths crush you!") — used in Phase 1 & Phase 2, STOPPED in Phase 3 per retail!
        if (arena.Phase < 3 && now >= arena.CdAnchorSmash && dist <= 75)
        {
            arena.CdAnchorSmash = now + 10.5;
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesAnchor);
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectAnchorDropEAS, tPos, now, 1.8);
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, tPos, ArtAnchorMarker, now + 1.55);
            var anchorAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectAnchorDropGA,
                scale: 1.75, range: 18, physToColdPct: 50);
            arena.PendingStrikes.Add(new PendingStrike
            {
                HitAt = now + 1.55,
                Center = tPos,
                Radius = 17,
                Attack = anchorAtk,
                ImpactArt = ArtAnchorSlam,
                AfterArt = ArtAnchorCrumble,
                SourceId = arena.BossId,
            });
            return;
        }

        // 2. Cannon Fire bombardment from PirateCannon1/2 via KalguurCannonballDaemon__ (cooldown 15s).
        if (now >= arena.CdCannonFire)
        {
            arena.CdCannonFire = now + 15.0;
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectSummonCannonFire, tPos, now, 1.5);
            await TriggerCannonBarrage(player, arena, boss, target, now, shells: arena.Phase >= 2 ? 7 : 5);
            return;
        }

        // 3. Dash Thrust + cold mortar cone ("Run you through!", cooldown 11s).
        if (now >= arena.CdDashThrust && dist >= 16 && dist <= 70)
        {
            arena.CdDashThrust = now + 11.0;
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesDash);
            var dashDest = SettlersBosses.WalkableNear(player, SettlersBosses.StepToward(bPos, tPos, Math.Max(14, dist - 5)));
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectDashThrust, dashDest, now, 1.2);
            await SettlersBosses.TeleportBoss(player, arena.BossId, boss, dashDest, now);
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, dashDest, ArtDashSlash, now + 0.9, rotation: (float)facing);
            var thrustAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectDashThrustHit,
                scale: 1.35, range: 16, physToColdPct: 100);
            await SettlersBosses.HitSegment(player, arena.BossId, boss, thrustAtk, bPos, dashDest, halfWidth: 8,
                () => !arena.Defeated && SettlersBosses.Alive(boss));
            var mortarAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectDashMortars,
                scale: 0.95, range: 55, physToColdPct: 100, forceSpell: true, projectile: DashMortarShot);
            SettlersBosses.LaunchBossSwing(player, arena.BossId, boss, mortarAtk, now, recoverySeconds: 0.3);
            return;
        }

        // 4. Water Geyser cast in Phase 2+ (cooldown 14s).
        if (arena.Phase >= 2 && now >= arena.CdGeyser)
        {
            arena.CdGeyser = now + 14.0;
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectSummonGeysers, tPos, now, 1.5);
            await SpawnGeyserMaze(player, arena, boss, now, count: 5);
            return;
        }

        // 5. Close range: Double Strike (75% cold) or Pirate Cold Melee (40% cold) + occasional agile Dodge.
        if (dist <= 18)
        {
            if (now >= arena.CdDoubleStrike)
            {
                arena.CdDoubleStrike = now + 5.5;
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesDoubleStrike);
                await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectDoubleStrike, tPos, now, 1.25);
                var dsAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectDoubleStrikeHit,
                    scale: 1.3, range: 18, physToColdPct: 75);
                await SettlersBosses.HitCone(player, arena.BossId, boss, dsAtk, bPos, facing, halfAngleRad: 0.75, maxRange: 18,
                    () => !arena.Defeated && SettlersBosses.Alive(boss));
                return;
            }
            if (now >= arena.CdDodge && AreaRng(player).NextDouble() < 0.28)
            {
                arena.CdDodge = now + 6.0;
                int dodgeEff = AreaRng(player).Next(3) switch { 0 => EffectDodgeLeft, 1 => EffectDodgeRight, _ => EffectDodgeBack };
                double sideAngle = dodgeEff == EffectDodgeLeft ? facing + Math.PI / 2
                    : dodgeEff == EffectDodgeRight ? facing - Math.PI / 2 : facing + Math.PI;
                var hop = SettlersBosses.WalkableNear(player, (
                    bPos.X + (int)Math.Round(Math.Cos(sideAngle) * 18),
                    bPos.Y + (int)Math.Round(Math.Sin(sideAngle) * 18)));
                await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, dodgeEff, hop, now, 0.55);
                await SettlersBosses.TeleportBoss(player, arena.BossId, boss, hop, now);
                return;
            }

            var meleeAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectColdMelee,
                scale: 1.0, range: 15, physToColdPct: 40);
            SettlersBosses.LaunchBossSwing(player, arena.BossId, boss, meleeAtk, now, recoverySeconds: 0.25);
            return;
        }

        await SettlersBosses.MoveBoss(player, arena.BossId, boss, tPos, now);
    }

    static async Task TriggerCannonBarrage(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now, int shells)
    {
        var rng = AreaRng(player);
        var tPos = target.Walk.Position(now);
        SpawnedMonster? daemonMonster = null;
        bool hasDaemon = arena.DaemonId != 0 && player.Area.Monsters.TryGetValue(arena.DaemonId, out daemonMonster);
        uint sourceId = hasDaemon ? arena.DaemonId : arena.BossId;
        var cannonAtk = hasDaemon
            ? SettlersBosses.BuildBossAttack(player, daemonMonster!, DaemonEffects, EffectCannonBallEDS,
                scale: 1.35, range: 16, physToFirePct: 40, forceSpell: true)
            : SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectSummonCannonFire,
                scale: 1.35, range: 16, physToFirePct: 40, forceSpell: true);

        // Flash ship broadside muzzles on PirateCannon1 / PirateCannon2.
        for (int c = 0; c < Math.Min(3, arena.Cannons.Count); c++)
        {
            var muzzleAt = arena.Cannons[(c + rng.Next(arena.Cannons.Count)) % arena.Cannons.Count];
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, muzzleAt, ArtCannonMuzzle, now + 0.9);
        }

        for (int i = 0; i < shells; i++)
        {
            var dropAt = i == 0
                ? tPos
                : SettlersBosses.WalkableNear(player, (
                    arena.Centre.X + rng.Next(-62, 63),
                    arena.Centre.Y + rng.Next(-62, 63)));
            double delay = 1.4 + i * 0.22;
            uint markerId = player.Instance.World.NewMonsterId();
            var sm = SettlersBosses.BaseStates(CannonballMarkerType)?.Set("armed", 1);
            foreach (var member in SettlersBosses.ActivePlayers(player))
                await SettlersBosses.CreateQuestObj(member, markerId, CannonballMarkerType, dropAt.X, dropAt.Y, 0f,
                    model: ArtCannonMarker, states: sm, hideMinimap: true);

            arena.PendingStrikes.Add(new PendingStrike
            {
                HitAt = now + delay,
                Center = dropAt,
                Radius = 13,
                Attack = cannonAtk,
                ImpactArt = ArtCannonImpact,
                MarkerObjId = markerId,
                MarkerObjType = CannonballMarkerType,
                SourceId = sourceId,
            });
        }
    }

    static async Task SpawnGeyserMaze(Player player, ArenaState arena, SpawnedMonster boss, double now, int count)
    {
        var rng = AreaRng(player);
        var geyserAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectGeyserDamage,
            scale: 1.1, range: 14, physToColdPct: 100, forceSpell: true);

        for (int i = 0; i < count; i++)
        {
            double angle = i * Math.Tau / Math.Max(1, count) + rng.NextDouble() * 0.4;
            double radius = 16 + rng.NextDouble() * 52;
            var at = SettlersBosses.WalkableNear(player, (
                arena.Centre.X + (int)Math.Round(Math.Cos(angle) * radius),
                arena.Centre.Y + (int)Math.Round(Math.Sin(angle) * radius)));

            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, at, ArtGeyserMarker, now + 1.35);
            uint geyserObjId = player.Instance.World.NewMonsterId();
            var sm = SettlersBosses.BaseStates(PirateGeyserType)?.Set("appear", 1);
            foreach (var member in SettlersBosses.ActivePlayers(player))
                await SettlersBosses.CreateQuestObj(member, geyserObjId, PirateGeyserType, at.X, at.Y, 0f,
                    model: ArtGeyser, states: sm, hideMinimap: true);

            arena.PendingStrikes.Add(new PendingStrike
            {
                HitAt = now + 1.35,
                Center = at,
                Radius = 11,
                Attack = geyserAtk,
                ImpactArt = ArtGeyser,
                MarkerObjId = geyserObjId,
                MarkerObjType = PirateGeyserType,
                AppliesSoaked = true,
                SourceId = arena.BossId,
            });
        }
    }

    static async Task SpawnTidalWaves(Player player, ArenaState arena, int count, double now, bool gauntlet)
    {
        if (arena.WaveMarkers.Count == 0) return;
        string varietyId = gauntlet ? WaveGauntletVarietyId : WaveVarietyId;
        var waveVariety = Monsters.Find(varietyId) ?? Monsters.Find(WaveVarietyId);
        if (waveVariety is not { Supported: true }) return;

        byte lvl = (byte)Math.Clamp(arena.Level, 1, 100);
        var waveSkills = SettlersBosses.BuildBossSkills(player, waveVariety, lvl, gauntlet ? WaveGauntletEffects : WaveEffects);
        var rng = AreaRng(player);
        int startIdx = rng.Next(arena.WaveMarkers.Count);
        for (int i = 0; i < count; i++)
        {
            var origin = arena.WaveMarkers[(startIdx + i * 3) % arena.WaveMarkers.Count];
            double dx = arena.Centre.X - origin.X;
            double dy = arena.Centre.Y - origin.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0) continue;
            dx /= len;
            dy /= len;
            float rot = (float)Math.Atan2(dy, dx);

            uint wid = await SpawnMonster(player, waveVariety, lvl,
                MonsterRarity.Normal, origin.X, origin.Y, log: false, alone: false, unrisen: false,
                rotation: rot, ownedSkills: waveSkills, scripted: true, nonBlocking: true, instanceOwned: true);
            if (wid == 0) continue;
            if (player.Area.Monsters.TryGetValue(wid, out var wm))
            {
                wm.DamageImmune = true;
                wm.NoRewards = true;
            }
            arena.ActiveWaves.Add(new ActiveWave
            {
                MonsterId = wid,
                Gauntlet = gauntlet,
                Pos = (origin.X, origin.Y),
                Dir = (dx, dy),
                Speed = 30.0,
                EndsAt = now + 5.8,
                NextMoveAt = now,
            });
        }
    }

    static async Task StepActiveWaves(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        if (arena.ActiveWaves.Count == 0) return;

        for (int i = arena.ActiveWaves.Count - 1; i >= 0; i--)
        {
            var wave = arena.ActiveWaves[i];
            if (now >= wave.EndsAt || !player.Area.Monsters.TryGetValue(wave.MonsterId, out var wm))
            {
                arena.ActiveWaves.RemoveAt(i);
                await SettlersBosses.DespawnMonster(player, wave.MonsterId);
                continue;
            }

            if (now < wave.NextMoveAt) continue;
            const double dt = 0.25;
            wave.NextMoveAt = now + dt;

            double nx = wave.Pos.X + wave.Dir.Dx * wave.Speed * dt;
            double ny = wave.Pos.Y + wave.Dir.Dy * wave.Speed * dt;
            wave.Pos = (nx, ny);
            var cell = ((int)Math.Round(nx), (int)Math.Round(ny));
            await SettlersBosses.MoveBoss(player, wave.MonsterId, wm, cell, now);

            // Damage + knockback each individual player swept by the Tidal Wave (per-player 0.75s cooldown, no party multi-hit!).
            MonsterAttack? waveAtk = null;
            foreach (var p in SettlersBosses.ActivePlayers(player))
            {
                if (p.PlayerLife == 0 || p.Paused || InGracePeriod(p)) continue;
                if (wave.PlayerHitCooldown.TryGetValue(p.Id, out double readyAt) && now < readyAt) continue;
                if (!SettlersBosses.Near(p.Walk.Position(now), cell, 11.5)) continue;
                wave.PlayerHitCooldown[p.Id] = now + 0.75;

                waveAtk ??= SettlersBosses.BuildBossAttack(player, wm,
                    wave.Gauntlet ? WaveGauntletEffects : WaveEffects,
                    wave.Gauntlet ? EffectTidalWaveGauntletDamage : EffectTidalWaveDamage,
                    scale: 1.05, range: 14, physToColdPct: 100, forceSpell: true);

                await SettlersBosses.HitPlayer(p, wave.MonsterId, wm, waveAtk,
                    at => SettlersBosses.Near(at, cell, 12.5),
                    () => !arena.Defeated && SettlersBosses.Alive(boss));
                // Apply tidal wave knockback along wave travel direction.
                var pPos = p.Walk.Position(now);
                var pushed = SettlersBosses.WalkableNear(p, (
                    pPos.X + (int)Math.Round(wave.Dir.Dx * 12),
                    pPos.Y + (int)Math.Round(wave.Dir.Dy * 12)));
                p.Walk.Teleport(pushed.X, pushed.Y, now);
                await SendWorldRecord(PlayerOf(p), p.Id, InstanceProtocol.Teleport(p.Id, pushed.X, pushed.Y), null);
            }
        }
    }

    static async Task StepPendingStrikes(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        for (int i = arena.PendingStrikes.Count - 1; i >= 0; i--)
        {
            var s = arena.PendingStrikes[i];
            if (now < s.HitAt) continue;
            arena.PendingStrikes.RemoveAt(i);

            if (s.MarkerObjId != 0 && s.MarkerObjType is not null)
            {
                var despawn = SettlersBosses.BaseStates(s.MarkerObjType)?.Set("despawn", 1);
                if (despawn is not null) await SettlersBosses.UpdateObjStates(player, s.MarkerObjId, s.MarkerObjType, despawn);
                foreach (var member in SettlersBosses.ActivePlayers(player))
                    await SendWorldRemoval(PlayerOf(member), s.MarkerObjId, null);
            }

            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, s.Center, s.ImpactArt, now + 1.2);
            if (s.AfterArt is not null)
                await SettlersBosses.SpawnTimedEffect(player, arena.Effects, s.Center, s.AfterArt, now + 2.0);

            var srcMonster = player.Area.Monsters.TryGetValue(s.SourceId, out var sm) ? sm : boss;
            await SettlersBosses.HitCircle(player, s.SourceId, srcMonster, s.Attack, s.Center, s.Radius,
                () => !arena.Defeated && SettlersBosses.Alive(boss),
                onPlayerHitAsync: s.AppliesSoaked ? hitPlayer => ApplySoakedStack(hitPlayer, arena.BossId, now) : null);
        }
    }

    static async Task ApplySoakedStack(Player player, uint bossId, double now)
    {
        var state = SettlersBosses.SoakedDebuffs.GetOrCreateValue(player);
        ushort oldBuff, newBuff;
        int stacks;
        lock (state.Lock)
        {
            if (!state.Counted)
            {
                state.Counted = true;
                Interlocked.Increment(ref SettlersBosses.SoakedHolders);
            }
            state.Stacks = Math.Min(SoakedMaxStacks, state.Stacks + 1);
            state.ExpiresAt = now + SoakedDurationSeconds;
            oldBuff = state.BuffInstance;
            // Allocate a fresh buff instance on refresh so the client resets the 20s HUD countdown bar.
            ushort bid = player.NextBuff++;
            newBuff = bid == 0 ? player.NextBuff++ : bid;
            state.BuffInstance = newBuff;
            stacks = state.Stacks;
        }
        if (oldBuff != 0)
            await SendWorldRecord(PlayerOf(player), player.Id, InstanceEntities.RemoveBuff(player.Id, oldBuff), null);
        await SendWorldRecord(PlayerOf(player), player.Id,
            InstanceEntities.AddBuff(player.Id, newBuff, SoakedBuffRow, bossId,
                u68: BitConverter.SingleToUInt32Bits((float)SoakedDurationSeconds), f6c: 1f, stacks: (ushort)stacks),
            null);
        await SettlersBosses.RefreshPlayerStats(player, "Soaked to the Bone");
    }

    static async Task TickSoakedExpiries(Player runner, double now)
    {
        if (Volatile.Read(ref SettlersBosses.SoakedHolders) == 0) return;
        foreach (var p in SettlersBosses.ActivePlayers(runner))
        {
            if (!SettlersBosses.SoakedDebuffs.TryGetValue(p, out var st)) continue;
            ushort removeBuff = 0;
            bool cleared = false;
            lock (st.Lock)
            {
                if (st.Stacks > 0 && (now >= st.ExpiresAt || p.PlayerLife == 0))
                {
                    st.Stacks = 0;
                    st.ExpiresAt = 0;
                    removeBuff = st.BuffInstance;
                    st.BuffInstance = 0;
                    if (st.Counted)
                    {
                        st.Counted = false;
                        Interlocked.Decrement(ref SettlersBosses.SoakedHolders);
                    }
                    cleared = true;
                }
            }
            if (cleared)
            {
                if (removeBuff != 0)
                    await SendWorldRecord(PlayerOf(p), p.Id, InstanceEntities.RemoveBuff(p.Id, removeBuff), null);
                await SettlersBosses.RefreshPlayerStats(p, "Soaked to the Bone expired");
            }
        }
    }

    internal static async Task OnMonsterKilled(MonsterKill kill)
    {
        if (kill.Monster.VarietyId != BossVarietyId) return;
        if (!Arenas.TryGetValue(kill.Player.Instance, out var arena)) return;
        await arena.Gate.WaitAsync();
        try
        {
            await CompleteVictory(kill.Player, arena, (kill.Monster.X, kill.Monster.Y));
        }
        finally { arena.Gate.Release(); }
    }

    static async Task CompleteVictory(Player player, ArenaState arena, (int X, int Y) killAt)
    {
        if (arena.Defeated) return;
        arena.Defeated = true;
        arena.InIntermission = false;
        arena.PendingStrikes.Clear();
        player.Instance.ContentState[SaveKey] = "defeated";

        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.AdmiralValeriusNpcRow, LinesDeath);
        await SettlersBosses.ClearEffects(player, arena.Effects);

        // Despawn all active tidal waves and cannonball daemon.
        foreach (var w in arena.ActiveWaves.ToArray())
            await SettlersBosses.DespawnMonster(player, w.MonsterId);
        arena.ActiveWaves.Clear();
        if (arena.DaemonId != 0)
        {
            await SettlersBosses.DespawnMonster(player, arena.DaemonId);
            arena.DaemonId = 0;
        }

        // Clear Soaked to the Bone debuff on all players.
        foreach (var p in SettlersBosses.ActivePlayers(player))
        {
            if (!SettlersBosses.SoakedDebuffs.TryGetValue(p, out var st)) continue;
            ushort removeBuff = 0;
            bool cleared = false;
            lock (st.Lock)
            {
                if (st.Stacks > 0 || st.Counted)
                {
                    st.Stacks = 0;
                    st.ExpiresAt = 0;
                    removeBuff = st.BuffInstance;
                    st.BuffInstance = 0;
                    if (st.Counted)
                    {
                        st.Counted = false;
                        Interlocked.Decrement(ref SettlersBosses.SoakedHolders);
                    }
                    cleared = true;
                }
            }
            if (cleared)
            {
                if (removeBuff != 0)
                    await SendWorldRecord(PlayerOf(p), p.Id, InstanceEntities.RemoveBuff(p.Id, removeBuff), null);
                await SettlersBosses.RefreshPlayerStats(p, "Soaked to the Bone cleared");
            }
        }

        // Open BossForceFieldDoor and SettlersBossPortal (return to Kingsmarch).
        await SetArenaSealed(player, arena, sealedState: false);

        // Set QuestFlag SettlersKilledAdmiralValerius + LeagueQuestFlag 1452 (Raulf dialogue).
        await SettlersBosses.GrantBossKillFlags(player, KilledQuestFlag, KilledLeagueQuestFlag);

        // Retail Sailor's Folly loot: massive owner-only Gold (VillageMapGoldDropsOnlyForOwner), Admiral's Arrogance chance, 1-2 Runegrafts!
        var dropAt = player.Layout.Near(killAt);
        int goldDropped = await SettlersBosses.DropBossGold(player, dropAt, minGold: 50000, maxGold: 65000, ownerOnly: true);
        await SettlersBosses.TryDropBossUnique(player, dropAt, arena.Level, UniqueDropPool, AdmiralsArroganceDropChance, scatterIndex: 13);
        await SettlersBosses.DropRunegrafts(player, dropAt, arena.Level, 1, 2, scatterBase: 15);

        await SettlersBossCallbacks.RaiseRescueShipment(new RescueShipmentEvent(player, AreaId, arena.Level, goldDropped));
        await SettlersBossCallbacks.RaiseBossDefeated(new SettlersBossDefeatedEvent(player, SettlersBossKind.AdmiralValerius, AreaId, arena.Level));
    }

    internal static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Arenas.TryGetValue(player.Instance, out var arena)) return false;
        return await SettlersBosses.TryUseSettlersBossPortal(player, use, requestedAt, arena.PortalId, arena.PortalAt, arena.PortalOpen);
    }

    internal static async Task OnPlayerLeaving(Player player)
    {
        if (Arenas.TryGetValue(player.Instance, out var arena))
        {
            await arena.Gate.WaitAsync();
            try { arena.PresentedPlayers.Remove(player); }
            finally { arena.Gate.Release(); }
        }
    }
}

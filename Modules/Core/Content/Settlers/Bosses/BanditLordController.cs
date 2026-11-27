using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Settlers.Bosses;

/// <summary>
/// Sasan, the Bandit Lord (<c>MonsterVarieties#10338</c>, <c>BanditArcherBoss</c>) in Abandoned Port
/// (<c>WorldAreas#1921</c>, <c>SettlersBossAlleyway</c>, level 83, <c>alleyway/rooms/arena.arm</c>).
/// <para>Retail 3.29.3c encounter mechanics:</para>
/// <list type="bullet">
/// <item>Arena objects: 27 destructible <c>RubbishStack_01</c> barricades (ignited by Sasan's fire skills into
/// <c>destroyed=1/3</c> burning -> <c>destroyed=2</c> collapsed + Burning Ground), 8 perimeter <c>FlareOriginMarker</c>s,
/// 3 central <c>FlareTargetMarker</c>s, <c>ArenaCentreMarker</c>, and <c>SettlersBossPortal</c> (<c>boss_aggro=2</c>
/// closed during combat, <c>1</c> open to Kingsmarch on kill).</item>
/// <item>Full retail skill kit: Explosive Arrow (<c>FuseArrowKalguur</c> stacking 2.0s fuses that detonate for fire AoE),
/// Multiattack Volley (<c>KalguurArcherTriggeredProjectiles</c>), Blink Arrow &amp; Evasive Rolls (Left/Right/Back),
/// Charged Snipe (<c>KalguurArcherSnipeLarge</c>, 60% fire), Firewake Arrow Combo (<c>KalguurArcherFireWake</c> twin
/// flame walls splitting outward perpendicular &amp; igniting rubbish carts), Circle of Flame (<c>FireCircle</c> ring +
/// interior arrow bombardment + rapid rubbish ignition <c>destroyed=3</c>), and Pull Arrows detonation.</item>
/// <item>50% HP Invulnerable Intermission: blinks to center (<c>flares=1</c>), launches tracking <c>BanditArcherFlare</c>
/// beacons from <c>FlareOriginMarker</c> toward <c>FlareTargetMarker</c> &amp; player, followed by deadly regional Hail of
/// Arrows bombardments (<c>arrowdown.ao</c> -> <c>arrowdown_impact.ao</c>).</item>
/// <item>Post-kill: sets <c>SettlersKilledBanditLord</c> + LeagueQuestFlag 1451 (unlocking Raulf's dialogue), reopens
/// <c>SettlersBossPortal</c>, invokes <see cref="SettlersBossCallbacks.OnRescueMappers"/>, and drops massive owner-only
/// Gold (40,000–55,000) + unique drop pool (<c>The Living Blade</c> / <c>The Bandit Lord's Band</c>) + Runegraft chance.</item>
/// </list>
/// </summary>
public static class BanditLordController
{
    public const string AreaId = "SettlersBossAlleyway";
    public const ushort AreaHash = 37779; // WorldAreas#1921
    public const int AreaLevel = 83;

    public const string BossVarietyId = "Metadata/Monsters/LeagueKalguur/BanditArcher/BanditArcherBoss"; // #10338
    public const string FlareOriginMarkerType = "Metadata/Monsters/LeagueKalguur/BanditArcher/FlareOriginMarker";
    public const string FlareTargetMarkerType = "Metadata/Monsters/LeagueKalguur/BanditArcher/FlareTargetMarker";
    public const string FlareObjectType = "Metadata/Monsters/LeagueKalguur/BanditArcher/BanditArcherFlare";
    public const string FireCircleType = "Metadata/Monsters/LeagueKalguur/BanditArcher/FireCircle";
    public const string FireWakeType = "Metadata/Monsters/LeagueKalguur/BanditArcher/KalguurArcherFireWake";
    public const string RubbishStackType = "Metadata/Terrain/Leagues/Settlers/BossArenas/Alleyway/RubbishStack_01";

    public const string KilledQuestFlag = "SettlersKilledBanditLord";
    public const int KilledLeagueQuestFlag = 1451; // Unlocks Raulf NPCTalk#3558 ("Sasan, the Bandit Lord")

    // Verified GrantedEffects rows from client.db (MonsterVarieties#10338).
    const int EffectExplosiveArrow = 12512;
    const int EffectMultiAttack = 12513;
    const int EffectSnipe = 12514;
    const int EffectSnipeLarge = 12535;
    const int EffectEmptySnipe = 12536;
    const int EffectFireWakeCombo = 12515;
    const int EffectBlinkArrow = 12518;
    const int EffectBlinkCentre = 12562;
    const int EffectRollLeft = 12519;
    const int EffectRollRight = 12520;
    const int EffectRollBack = 12521;
    const int EffectCircleOfDeath = 12522;
    const int EffectFireCircleFire = 12526;
    const int EffectFireCircleBombard = 12530;
    const int EffectPullArrows = 12538;
    const int EffectTriggeredExplosion = 12534;
    const int EffectGroundFire = 12550;

    static readonly int[] BossEffects =
    [
        EffectExplosiveArrow, EffectMultiAttack, EffectSnipe, EffectSnipeLarge, EffectEmptySnipe,
        EffectFireWakeCombo, EffectBlinkArrow, EffectBlinkCentre,
        EffectRollLeft, EffectRollRight, EffectRollBack,
        EffectCircleOfDeath, EffectFireCircleFire, EffectFireCircleBombard,
        EffectPullArrows, EffectTriggeredExplosion, EffectGroundFire,
    ];

    // Verified .ao paths from data/game-files.zip.
    const string ArtRoot = "Metadata/Effects/Spells/monsters_effects/LeagueKalguur/BanditArcherBoss/";
    const string ArtBlinkArrowDown = ArtRoot + "blink_arrow/arrowdown.ao";
    const string ArtHailArrowDown = ArtRoot + "circleflame_deathlyarrows/arrowdown/arrowdown.ao";
    const string ArtHailImpact = ArtRoot + "circleflame_deathlyarrows/arrowdown_impact/arrowdown_impact.ao";
    const string ArtCircleFlame = ArtRoot + "circleflame_deathlyarrows/circleflame/rig.ao";
    const string ArtDetonateWave = ArtRoot + "detonate_wave.ao";
    const string ArtExplodingImpact = ArtRoot + "exploding_impact.ao";
    const string ArtIndicator = ArtRoot + "indicator.ao";
    const string ArtFlare = ArtRoot + "marked_for_seige/flare/flare.ao";
    const string ArtBunchOfArrows = ArtRoot + "marked_for_seige/bunchofarrows/bunchofarrows.ao";
    const string ArtFireWake = ArtRoot + "epicburningarrow/quarterpipeflames/quarterpipeflames_01.ao";

    // Verified projectiles from Projectiles.dat (#1323, #715, #1324).
    static readonly SkillCatalog.MonsterProjectileInfo ExplosiveArrowShot =
        new("Metadata/Projectiles/FuseArrowKalguur", 3260, Lobbed: false, 1);

    static readonly SkillCatalog.MonsterProjectileInfo MultiAttackShot =
        new("Metadata/Projectiles/FuseArrowKalguur", 3260, Lobbed: false, 1)
        {
            Count = 3,
            SpreadRadians = 0.45,
        };

    static readonly SkillCatalog.MonsterProjectileInfo SnipeShot =
        new("Metadata/Projectiles/KalguurArcherSnipeLarge", 3260, Lobbed: false, 2);

    // Retail voice lines (NPCTextAudio, NPCs#680).
    static readonly string[] LinesEmerge = ["BanditArcherBoss_Emerge_Random", "BanditArcherBoss_Emerge_2", "BanditArcherBoss_Emerge_3"];
    static readonly string[] LinesSnipeStart = ["BanditArcherBoss_BarrageToSnipe_Start_Random", "BanditArcherBoss_BarrageToSnipe_Start_3"];
    static readonly string[] LinesSnipeEnd = ["BanditArcherBoss_BarrageToSnipe_End_Random", "BanditArcherBoss_BarrageToSnipe_End_4"];
    static readonly string[] LinesCircleFlame = ["BanditArcherBoss_CircleFlame_Random", "BanditArcherBoss_CircleFlame_3", "BanditArcherBoss_CircleFlame_5"];
    static readonly string[] LinesFireWake = ["BanditArcherBoss_DoubleFireArrow_Random", "BanditArcherBoss_DoubleFireArrow_3"];
    static readonly string[] LinesFlare = ["BanditArcherBoss_FlareSignal_Random", "BanditArcherBoss_FlareSignal_3", "BanditArcherBoss_FlareSignal_4"];
    static readonly string[] LinesPull = ["BanditArcherBoss_PullFire_Random", "BanditArcherBoss_PullFire_3", "BanditArcherBoss_PullFire_5"];
    static readonly string[] LinesRoll = ["BanditArcherBoss_Roll_Random", "BanditArcherBoss_Roll_5", "BanditArcherBoss_Roll_6"];
    static readonly string[] LinesDeath = ["BanditArcherBoss_Death_Random", "BanditArcherBoss_Death_2", "BanditArcherBoss_Death_3"];

    // Retail 3.28/3.29 unique drop pool: The Living Blade (Ezomyte Blade) & The Bandit Lord's Band (Gold Ring).
    public static readonly (string Name, int Weight)[] UniqueDropPool =
    [
        ("The Living Blade", 65),
        ("The Bandit Lord's Band", 35),
    ];
    public const double UniqueDropChance = 0.25;

    static readonly int[] PhaseThresholds = [50];
    const string SaveKey = "Settlers/boss_bandit_lord";

    // Exact fallback coordinates from alleyway/rooms/arena.arm.
    static readonly (int X, int Y) DefaultCentre = (419, 368);
    static readonly (int X, int Y) DefaultBossSpawn = (452, 356);
    const float DefaultBossRotation = 4.46804f;
    static readonly (int X, int Y) DefaultPortalAt = (274, 420);
    const float DefaultPortalRot = 1.62138f;

    static readonly (int X, int Y)[] DefaultFlareOrigins =
    [
        (516, 416), (507, 315), (333, 326), (330, 422),
        (386, 428), (452, 425), (449, 313), (385, 315),
    ];

    static readonly (int X, int Y)[] DefaultFlareTargets = [(419, 368), (388, 369), (449, 368)];

    sealed class RubbishPile
    {
        public required uint Id { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required float Rotation { get; init; }
        public required string Model { get; init; }
        public int State; // 0 = intact blocking, 1/3 = burning, 2 = collapsed & unblocked (burning ground)
        public double CollapseAt;
    }

    sealed class BurningPatch
    {
        public required (int X, int Y) Center { get; init; }
        public required double Radius { get; init; }
        public required double EndsAt { get; init; }
        public double NextTickAt;
    }

    sealed class ActiveFireCircle
    {
        public required uint ObjId { get; init; }
        public required (int X, int Y) Center { get; init; }
        public required double Radius { get; init; }
        public required double EndsAt { get; init; }
        public double NextBombardAt;
        public bool EnteredIdle;
    }

    sealed class PendingBombard
    {
        public required double HitAt { get; init; }
        public required (int X, int Y) Center { get; init; }
        public required double Radius { get; init; }
        public required MonsterAttack Attack { get; init; }
        public required string ImpactArt { get; init; }
        public bool IgnitesRubbish { get; init; }
        public bool FastRubbishBurn { get; init; }
        public uint CleanupObjId { get; init; }
        public string? CleanupObjType { get; init; }
    }

    sealed class PendingFuse
    {
        public required Player Target { get; init; }
        public required double DetonateAt { get; init; }
        public int Stacks = 1;
    }

    sealed class ArenaState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public (int X, int Y) Centre = DefaultCentre;
        public (int X, int Y) PortalAt = DefaultPortalAt;
        public float PortalRot = DefaultPortalRot;
        public uint PortalId;
        public bool PortalOpen = true;

        public readonly List<(int X, int Y)> FlareOrigins = [];
        public readonly List<(int X, int Y)> FlareTargets = [];
        public readonly List<RubbishPile> Rubbish = [];

        public uint BossId;
        public int Level = AreaLevel;
        public bool Engaged;
        public bool Defeated;
        public int Phase = 1; // 1 (100-50%), 2 (50-0%)
        public bool InIntermission;
        public double IntermissionEndsAt;
        public double NextFlareWaveAt;

        // Per-skill cooldowns.
        public double CdMultiAttack;
        public double CdSnipe;
        public double CdFireWake;
        public double CdCircleFlame;
        public double CdBlink;
        public double CdRoll;
        public double CdFlarePhase2;

        public readonly List<ActiveFireCircle> FireCircles = [];
        public readonly List<BurningPatch> BurningGrounds = [];
        public readonly List<PendingBombard> PendingBombards = [];
        public readonly List<PendingFuse> PendingFuses = [];
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
            PortalId = inst.World.NewMonsterId(),
        };
        if (inst.ContentState.TryGetValue(SaveKey, out string? saved) && saved == "defeated")
            state.Defeated = true;

        var roomObjects = player.Layout.Level?.RoomObjects ?? [];
        if (roomObjects.FirstOrDefault(o => o.Type == SettlersBosses.ArenaCentreMarkerType) is { } ctr)
            state.Centre = (ctr.X, ctr.Y);
        if (roomObjects.FirstOrDefault(o => o.Type == SettlersBosses.SettlersBossPortalType) is { } portal)
        {
            state.PortalAt = (portal.X, portal.Y);
            state.PortalRot = portal.Rotation;
        }

        var origins = roomObjects.Where(o => o.Type == FlareOriginMarkerType).Select(o => (o.X, o.Y)).ToList();
        var targets = roomObjects.Where(o => o.Type == FlareTargetMarkerType).Select(o => (o.X, o.Y)).ToList();
        state.FlareOrigins.AddRange(origins.Count > 0 ? origins : DefaultFlareOrigins);
        state.FlareTargets.AddRange(targets.Count > 0 ? targets : DefaultFlareTargets);

        foreach (var r in roomObjects.Where(o => o.Type == RubbishStackType))
        {
            state.Rubbish.Add(new RubbishPile
            {
                Id = inst.World.NewMonsterId(),
                X = r.X,
                Y = r.Y,
                Rotation = r.Rotation,
                Model = string.IsNullOrEmpty(r.Model) ? "Metadata/Terrain/Leagues/Settlers/BossArenas/Alleyway/RubbishStack_01.ao" : r.Model,
                State = 0,
            });
        }
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
                    if (m.Variety?.Id == BossVarietyId)
                        await SettlersBosses.DespawnMonster(player, id);
                return;
            }

            var spawned = await SettlersBosses.EnsureScriptedBoss(
                player, BossVarietyId, (byte)Math.Clamp(arena.Level, 1, 100),
                DefaultBossSpawn, DefaultBossRotation, BossEffects);
            if (spawned is { } b)
            {
                arena.BossId = b.Id;
                b.Monster.LifePhases = new LifePhaseGate(PhaseThresholds);
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
            await SettlersBosses.CreateBossPortal(player, arena.PortalId, arena.PortalAt.X, arena.PortalAt.Y, arena.PortalRot, arena.PortalOpen);

            foreach (var r in arena.Rubbish)
            {
                var sm = SettlersBosses.BaseStates(RubbishStackType)?.Set("destroyed", r.State);
                bool blocked = r.State != 2;
                await SettlersBosses.CreateQuestObj(player, r.Id, RubbishStackType, r.X, r.Y, r.Rotation,
                    model: r.Model, blocked: blocked, states: sm, hideMinimap: true);
                if (blocked) SettlersBosses.SetDiscBarrier(player, r.Id, r.X, r.Y, 3, true);
            }
        }
        finally { arena.Gate.Release(); }
    }

    static async Task SetPortalOpen(Player runner, ArenaState arena, bool open)
    {
        arena.PortalOpen = open;
        var sm = SettlersBosses.BaseStates(SettlersBosses.SettlersBossPortalType)?.Set("boss_aggro", open ? 1 : 2);
        if (sm is not null)
            await SettlersBosses.UpdateObjStates(runner, arena.PortalId, SettlersBosses.SettlersBossPortalType, sm);
    }

    internal static async Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        if (!Arenas.TryGetValue(player.Instance, out var arena)) return;
        if (arena.Defeated && arena.Effects.Count == 0 && arena.PendingBombards.Count == 0 && arena.FireCircles.Count == 0) return;

        if (!arena.Gate.Wait(0)) return;
        try
        {
            double now = AreaTime(player);
            await SettlersBosses.ExpireEffects(player, arena.Effects, now);

            if (arena.BossId == 0 || !player.Area.Monsters.TryGetValue(arena.BossId, out var boss) || !SettlersBosses.Alive(boss))
            {
                if (!arena.Defeated && arena.BossId != 0)
                    await CompleteVictory(player, arena, arena.Centre);
                return;
            }

            await StepRubbishCollapse(player, arena, now);
            await StepPendingBombards(player, arena, boss, now);
            await StepFireCircles(player, arena, boss, now);
            await StepPendingFuses(player, arena, boss, now);
            await StepBurningGrounds(player, arena, boss, now);

            // Let active intermissions complete naturally on timer (never skip a phase on death or entry grace period).
            if (arena.InIntermission && now >= arena.IntermissionEndsAt)
            {
                await EndIntermission(player, arena, boss);
                return;
            }

            if (!player.AiEnabled) return;

            var target = SettlersBosses.NearestLivingPlayer(player, boss.Walk.Position(now), now, maxRange: 190, awakeId: arena.BossId);
            if (target is null) return;

            if (!arena.Engaged)
            {
                if (!SettlersBosses.Near(target.Walk.Position(now), boss.Walk.Position(now), 125)) return;
                arena.Engaged = true;
                boss.Aggro = true;
                await SetPortalOpen(player, arena, open: false);
                await SettlersBosses.SetBossStates(player, arena.BossId,
                    ("boss_life_bar", 1), ("activated", 1), ("arrow_visible", 1), ("global_first_aggro", 1));
                await SettlersBosses.EnsureBossBar(player, arena.BossId, boss);
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesEmerge);
                boss.BusyUntil = now + 1.3;
                return;
            }

            // Check 50% HP intermission (Flare Arrows + Hail of Arrows bombardment!).
            if (!arena.InIntermission
                && boss.LifePhases is { Active: false } phases
                && phases.Begin(boss.Life, boss.MaxLife))
            {
                await StartIntermission(player, arena, boss, target, now);
                return;
            }

            if (arena.InIntermission)
            {
                await StepIntermission(player, arena, boss, target, now);
                return;
            }

            if (now < boss.BusyUntil) return;
            await ActCombat(player, arena, boss, target, now);
        }
        finally { arena.Gate.Release(); }
    }

    static async Task StartIntermission(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        arena.InIntermission = true;
        arena.IntermissionEndsAt = now + 9.5;
        arena.NextFlareWaveAt = now + 0.9;

        SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, true);
        await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectBlinkCentre, arena.Centre, now, 1.3);
        await SettlersBosses.TeleportBoss(player, arena.BossId, boss, arena.Centre, now);
        await SettlersBosses.SetBossStates(player, arena.BossId, ("flares", 1));
        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesFlare);

        await LaunchFlareBarrage(player, arena, boss, target, now, flares: 4);
    }

    static async Task StepIntermission(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        if (now >= arena.IntermissionEndsAt)
        {
            await EndIntermission(player, arena, boss);
            return;
        }
        if (now < arena.NextFlareWaveAt) return;
        arena.NextFlareWaveAt = now + 2.4;
        await LaunchFlareBarrage(player, arena, boss, target, now, flares: 4);
    }

    static async Task EndIntermission(Player player, ArenaState arena, SpawnedMonster boss)
    {
        arena.InIntermission = false;
        arena.Phase = 2;
        SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, false);
        boss.LifePhases?.Finish();
        await SettlersBosses.SetBossStates(player, arena.BossId, ("flares", 0));
    }

    static async Task LaunchFlareBarrage(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now, int flares)
    {
        var rng = AreaRng(player);
        var tPos = target.Walk.Position(now);
        var hailAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectFireCircleBombard,
            scale: 1.45, range: 22, physToFirePct: 60);

        // Launch signal flare from rooftop FlareOriginMarker toward marked bombardment zones.
        if (arena.FlareOrigins.Count > 0)
        {
            var originAt = arena.FlareOrigins[rng.Next(arena.FlareOrigins.Count)];
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, originAt, ArtFlare, now + 1.5);
        }

        for (int i = 0; i < flares; i++)
        {
            var markAt = i == 0
                ? tPos
                : i - 1 < arena.FlareTargets.Count && rng.NextDouble() < 0.45
                    ? arena.FlareTargets[(i - 1) % arena.FlareTargets.Count]
                    : SettlersBosses.WalkableNear(player, (
                        arena.Centre.X + rng.Next(-68, 69),
                        arena.Centre.Y + rng.Next(-52, 53)));

            uint flareId = player.Instance.World.NewMonsterId();
            foreach (var member in SettlersBosses.ActivePlayers(player))
                await SettlersBosses.CreateQuestObj(member, flareId, FlareObjectType, markAt.X, markAt.Y, 0f,
                    model: ArtFlare, transitionState: 3, hideMinimap: true);

            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, markAt, ArtHailArrowDown, now + 1.65);
            arena.PendingBombards.Add(new PendingBombard
            {
                HitAt = now + 1.65,
                Center = markAt,
                Radius = 18,
                Attack = hailAtk,
                ImpactArt = ArtHailImpact,
                IgnitesRubbish = true,
                CleanupObjId = flareId,
                CleanupObjType = FlareObjectType,
            });
        }
    }

    static async Task ActCombat(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        var bPos = boss.Walk.Position(now);
        var tPos = target.Walk.Position(now);
        double dist = SettlersBosses.Distance(bPos, tPos);
        double facing = Math.Atan2(tPos.Y - bPos.Y, tPos.X - bPos.X);

        // 1. Circle of Flame ("You're Trapped!" / "Cooked alive!", cooldown 18s).
        if (now >= arena.CdCircleFlame && dist <= 85)
        {
            arena.CdCircleFlame = now + (arena.Phase >= 2 ? 15.0 : 19.0);
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesCircleFlame);
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectCircleOfDeath, tPos, now, 1.8);
            uint circleId = player.Instance.World.NewMonsterId();
            foreach (var member in SettlersBosses.ActivePlayers(player))
                await SettlersBosses.CreateQuestObj(member, circleId, FireCircleType, tPos.X, tPos.Y, 0f,
                    model: ArtCircleFlame, transitionState: 2, hideMinimap: true);
            arena.FireCircles.Add(new ActiveFireCircle
            {
                ObjId = circleId,
                Center = tPos,
                Radius = 24,
                EndsAt = now + 5.5,
                NextBombardAt = now + 1.0,
            });
            await IgniteRubbishNear(player, arena, tPos, radius: 26, now, fast: true);
            return;
        }

        // 2. Firewake Arrow Combo (twin flame walls that split outward perpendicular, cooldown 14s).
        if (now >= arena.CdFireWake && dist <= 90)
        {
            arena.CdFireWake = now + (arena.Phase >= 2 ? 12.0 : 15.0);
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesFireWake);
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectFireWakeCombo, tPos, now, 2.0);
            await TriggerFireWakeCombo(player, arena, boss, bPos, facing, now);
            return;
        }

        // 3. Charged Snipe ("Die!" / "Meet your maker!", high-speed piercing fire arrow, cooldown 12s).
        if (now >= arena.CdSnipe && dist <= 95)
        {
            arena.CdSnipe = now + (arena.Phase >= 2 ? 10.0 : 13.0);
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesSnipeStart);
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectEmptySnipe, tPos, now, 1.6);
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, tPos, ArtIndicator, now + 1.25);
            var snipeAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectSnipeLarge,
                scale: 1.85, range: 95, physToFirePct: 60, projectile: SnipeShot);
            arena.PendingBombards.Add(new PendingBombard
            {
                HitAt = now + 1.25,
                Center = tPos,
                Radius = 11,
                Attack = snipeAtk,
                ImpactArt = ArtExplodingImpact,
                IgnitesRubbish = true,
            });
            return;
        }

        // 4. Phase 2 periodic Flare Hail of Arrows (cooldown 16s).
        if (arena.Phase >= 2 && now >= arena.CdFlarePhase2)
        {
            arena.CdFlarePhase2 = now + 16.0;
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesFlare);
            await LaunchFlareBarrage(player, arena, boss, target, now, flares: 3);
            boss.BusyUntil = now + 1.2;
            return;
        }

        // 5. Evasive Roll or Blink Arrow if player closes into melee range.
        if (dist < 22 && now >= arena.CdRoll)
        {
            arena.CdRoll = now + 6.0;
            int rollEff = AreaRng(player).Next(3) switch { 0 => EffectRollLeft, 1 => EffectRollRight, _ => EffectRollBack };
            double away = rollEff == EffectRollBack ? facing + Math.PI : facing + (rollEff == EffectRollLeft ? 1.3 : -1.3);
            var dest = SettlersBosses.WalkableNear(player, (
                bPos.X + (int)Math.Round(Math.Cos(away) * 28),
                bPos.Y + (int)Math.Round(Math.Sin(away) * 28)));
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesRoll);
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, rollEff, dest, now, 0.75);
            await SettlersBosses.TeleportBoss(player, arena.BossId, boss, dest, now);
            return;
        }

        if (dist > 68 && now >= arena.CdBlink)
        {
            arena.CdBlink = now + 8.0;
            var blinkSpot = SettlersBosses.WalkableNear(player, SettlersBosses.StepToward(bPos, tPos, 35));
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectBlinkArrow, blinkSpot, now, 1.0);
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, blinkSpot, ArtBlinkArrowDown, now + 0.9);
            await SettlersBosses.TeleportBoss(player, arena.BossId, boss, blinkSpot, now);
            return;
        }

        // 6. Multiattack Volley (3 Explosive Arrows, cooldown 6s).
        if (now >= arena.CdMultiAttack && dist <= 85)
        {
            arena.CdMultiAttack = now + (arena.Phase >= 2 ? 5.0 : 6.5);
            var multiAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectMultiAttack,
                scale: 0.9, range: 85, physToFirePct: 50, projectile: MultiAttackShot);
            SettlersBosses.LaunchBossSwing(player, arena.BossId, boss, multiAtk, now, recoverySeconds: 0.35);
            QueueExplosiveFuse(arena, target, now, stacks: 2);
            return;
        }

        // 7. Default attack: Explosive Arrow (sticks 2.0s fuse that detonates!).
        if (dist <= 80)
        {
            var eaAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectExplosiveArrow,
                scale: 0.95, range: 80, physToFirePct: 40, projectile: ExplosiveArrowShot);
            SettlersBosses.LaunchBossSwing(player, arena.BossId, boss, eaAtk, now, recoverySeconds: 0.30);
            QueueExplosiveFuse(arena, target, now, stacks: 1);
            return;
        }

        await SettlersBosses.MoveBoss(player, arena.BossId, boss, tPos, now);
    }

    static async Task TriggerFireWakeCombo(Player player, ArenaState arena, SpawnedMonster boss, (int X, int Y) origin, double facing, double now)
    {
        var wakeAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectFireWakeCombo,
            scale: 1.4, range: 14, physToFirePct: 100, forceSpell: true);
        double cos = Math.Cos(facing), sin = Math.Sin(facing);
        double perpX = -sin, perpY = cos;

        // First arrow creates two flame corridors along path; second splits them outward perpendicular!
        for (int step = 1; step <= 4; step++)
        {
            double fwd = step * 14.0;
            for (int side = -1; side <= 1; side += 2)
            {
                var wallPt = SettlersBosses.WalkableNear(player, (
                    origin.X + (int)Math.Round(cos * fwd + perpX * side * 9),
                    origin.Y + (int)Math.Round(sin * fwd + perpY * side * 9)));
                var splitPt = SettlersBosses.WalkableNear(player, (
                    origin.X + (int)Math.Round(cos * fwd + perpX * side * 22),
                    origin.Y + (int)Math.Round(sin * fwd + perpY * side * 22)));

                await SettlersBosses.SpawnTimedEffect(player, arena.Effects, wallPt, ArtFireWake, now + 1.5, rotation: (float)facing);
                arena.PendingBombards.Add(new PendingBombard
                {
                    HitAt = now + 0.75 + step * 0.12,
                    Center = wallPt,
                    Radius = 10,
                    Attack = wakeAtk,
                    ImpactArt = ArtExplodingImpact,
                    IgnitesRubbish = true,
                });
                arena.PendingBombards.Add(new PendingBombard
                {
                    HitAt = now + 1.55 + step * 0.12,
                    Center = splitPt,
                    Radius = 11,
                    Attack = wakeAtk,
                    ImpactArt = ArtFireWake,
                    IgnitesRubbish = true,
                });
            }
        }
    }

    static void QueueExplosiveFuse(ArenaState arena, Player target, double now, int stacks)
    {
        var existing = arena.PendingFuses.FirstOrDefault(f => ReferenceEquals(f.Target, target));
        if (existing is not null)
        {
            existing.Stacks = Math.Min(10, existing.Stacks + stacks);
            return;
        }
        arena.PendingFuses.Add(new PendingFuse
        {
            Target = target,
            DetonateAt = now + 2.0, // Retail base_skill_effect_duration = 2000ms
            Stacks = stacks,
        });
    }

    static async Task StepPendingFuses(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        for (int i = arena.PendingFuses.Count - 1; i >= 0; i--)
        {
            var fuse = arena.PendingFuses[i];
            if (now < fuse.DetonateAt) continue;
            arena.PendingFuses.RemoveAt(i);
            if (fuse.Target.Closed || fuse.Target.LeavingArea || fuse.Target.PlayerLife == 0) continue;

            var pos = fuse.Target.Walk.Position(now);
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, pos, ArtDetonateWave, now + 0.8);
            var detAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectTriggeredExplosion,
                scale: 0.75 + 0.25 * fuse.Stacks, range: 12 + fuse.Stacks, physToFirePct: 85, forceSpell: true);
            await SettlersBosses.HitCircle(player, arena.BossId, boss, detAtk, pos, 10 + fuse.Stacks,
                () => !arena.Defeated && SettlersBosses.Alive(boss));
            await IgniteRubbishNear(player, arena, pos, 12, now, fast: false);
        }
    }

    static async Task StepFireCircles(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        if (arena.FireCircles.Count == 0) return;
        MonsterAttack? ringAtk = null, rainAtk = null;

        for (int i = arena.FireCircles.Count - 1; i >= 0; i--)
        {
            var fc = arena.FireCircles[i];
            if (now >= fc.EndsAt)
            {
                arena.FireCircles.RemoveAt(i);
                await SettlersBosses.UpdateObjTransition(player, fc.ObjId, FireCircleType, 4);
                foreach (var member in SettlersBosses.ActivePlayers(player))
                    await SendWorldRemoval(PlayerOf(member), fc.ObjId, null);
                continue;
            }
            if (!fc.EnteredIdle)
            {
                fc.EnteredIdle = true;
                await SettlersBosses.UpdateObjTransition(player, fc.ObjId, FireCircleType, 3);
            }
            if (now < fc.NextBombardAt) continue;
            fc.NextBombardAt = now + 1.0;

            ringAtk ??= SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectFireCircleFire,
                scale: 1.0, range: 26, physToFirePct: 100, forceSpell: true);
            rainAtk ??= SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectFireCircleBombard,
                scale: 1.15, range: 14, physToFirePct: 60);

            // Perimeter fire ring damages players touching the fire boundary.
            await SettlersBosses.HitRing(player, arena.BossId, boss, ringAtk, fc.Center,
                fc.Radius - 4.5, fc.Radius + 4.5, () => !arena.Defeated && SettlersBosses.Alive(boss));

            // Rain arrows inside the circle.
            var rng = AreaRng(player);
            for (int k = 0; k < 3; k++)
            {
                double a = rng.NextDouble() * Math.Tau;
                double r = rng.NextDouble() * (fc.Radius - 4);
                var pt = (fc.Center.X + (int)Math.Round(Math.Cos(a) * r), fc.Center.Y + (int)Math.Round(Math.Sin(a) * r));
                await SettlersBosses.SpawnTimedEffect(player, arena.Effects, pt, ArtBunchOfArrows, now + 0.7);
                arena.PendingBombards.Add(new PendingBombard
                {
                    HitAt = now + 0.7,
                    Center = pt,
                    Radius = 9,
                    Attack = rainAtk,
                    ImpactArt = ArtHailImpact,
                    IgnitesRubbish = true,
                    FastRubbishBurn = true,
                });
            }
        }
    }

    static async Task StepPendingBombards(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        for (int i = arena.PendingBombards.Count - 1; i >= 0; i--)
        {
            var b = arena.PendingBombards[i];
            if (now < b.HitAt) continue;
            arena.PendingBombards.RemoveAt(i);

            if (b.CleanupObjId != 0 && b.CleanupObjType is not null)
            {
                await SettlersBosses.UpdateObjTransition(player, b.CleanupObjId, b.CleanupObjType, 4);
                foreach (var member in SettlersBosses.ActivePlayers(player))
                    await SendWorldRemoval(PlayerOf(member), b.CleanupObjId, null);
            }

            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, b.Center, b.ImpactArt, now + 1.1);
            await SettlersBosses.HitCircle(player, arena.BossId, boss, b.Attack, b.Center, b.Radius,
                () => !arena.Defeated && SettlersBosses.Alive(boss));
            if (b.IgnitesRubbish)
                await IgniteRubbishNear(player, arena, b.Center, b.Radius + 4, now, b.FastRubbishBurn);
        }
    }

    static async Task IgniteRubbishNear(Player player, ArenaState arena, (int X, int Y) center, double radius, double now, bool fast)
    {
        foreach (var r in arena.Rubbish)
        {
            if (r.State != 0) continue;
            if (!SettlersBosses.Near((r.X, r.Y), center, radius)) continue;
            // RubbishStack_01.ot: destroyed=1 (normal burn) or destroyed=3 (special case for fire circle to break quicker).
            r.State = fast ? 3 : 1;
            r.CollapseAt = now + (fast ? 2.0 : 4.0);
            var sm = SettlersBosses.BaseStates(RubbishStackType)?.Set("destroyed", r.State);
            if (sm is not null) await SettlersBosses.UpdateObjStates(player, r.Id, RubbishStackType, sm);
        }
    }

    static async Task StepRubbishCollapse(Player player, ArenaState arena, double now)
    {
        foreach (var r in arena.Rubbish)
        {
            if (r.State is not (1 or 3) || now < r.CollapseAt) continue;
            r.State = 2; // collapsed_01 -> end_01, blockage opens, leaves Burning Ground!
            var sm = SettlersBosses.BaseStates(RubbishStackType)?.Set("destroyed", 2);
            if (sm is not null) await SettlersBosses.UpdateObjStates(player, r.Id, RubbishStackType, sm);
            foreach (var member in SettlersBosses.ActivePlayers(player))
            {
                SettlersBosses.SetDiscBarrier(member, r.Id, r.X, r.Y, 3, false);
                await SendWorldRecord(PlayerOf(member), r.Id, InstanceEntities.BlockageUpdate(r.Id, false), null);
            }
            arena.BurningGrounds.Add(new BurningPatch
            {
                Center = (r.X, r.Y),
                Radius = 11,
                EndsAt = now + 22.0,
                NextTickAt = now + 0.5,
            });
        }
    }

    static async Task StepBurningGrounds(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        if (arena.BurningGrounds.Count == 0) return;
        MonsterAttack? burnAtk = null;

        for (int i = arena.BurningGrounds.Count - 1; i >= 0; i--)
        {
            var patch = arena.BurningGrounds[i];
            if (now >= patch.EndsAt)
            {
                arena.BurningGrounds.RemoveAt(i);
                continue;
            }
            if (now < patch.NextTickAt) continue;
            patch.NextTickAt = now + 0.75;
            burnAtk ??= SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectGroundFire,
                scale: 0.45, range: 12, physToFirePct: 100, forceSpell: true);
            await SettlersBosses.HitCircle(player, arena.BossId, boss, burnAtk, patch.Center, patch.Radius,
                () => !arena.Defeated && SettlersBosses.Alive(boss));
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
        arena.PendingBombards.Clear();
        arena.PendingFuses.Clear();
        arena.BurningGrounds.Clear();
        player.Instance.ContentState[SaveKey] = "defeated";

        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BanditLordNpcRow, LinesDeath);
        await SettlersBosses.ClearEffects(player, arena.Effects);

        foreach (var fc in arena.FireCircles.ToArray())
        {
            await SettlersBosses.UpdateObjTransition(player, fc.ObjId, FireCircleType, 4);
            foreach (var member in SettlersBosses.ActivePlayers(player))
                await SendWorldRemoval(PlayerOf(member), fc.ObjId, null);
        }
        arena.FireCircles.Clear();

        // Reopen SettlersBossPortal (return to Kingsmarch).
        await SetPortalOpen(player, arena, open: true);

        // Set QuestFlag SettlersKilledBanditLord + LeagueQuestFlag 1451 (Raulf dialogue).
        await SettlersBosses.GrantBossKillFlags(player, KilledQuestFlag, KilledLeagueQuestFlag);

        // Retail Abandoned Port loot: massive owner-only Gold (VillageMapGoldDropsOnlyForOwner), The Living Blade / The Bandit Lord's Band, Runegraft chance!
        var dropAt = player.Layout.Near(killAt);
        int goldDropped = await SettlersBosses.DropBossGold(player, dropAt, minGold: 40000, maxGold: 55000, ownerOnly: true);
        await SettlersBosses.TryDropBossUnique(player, dropAt, arena.Level, UniqueDropPool, UniqueDropChance, scatterIndex: 13);
        if (AreaRng(player).NextDouble() < 0.50)
            await SettlersBosses.DropRunegrafts(player, dropAt, arena.Level, 1, 1, scatterBase: 15);

        await SettlersBossCallbacks.RaiseRescueMappers(new RescueMappersEvent(player, AreaId, arena.Level, goldDropped));
        await SettlersBossCallbacks.RaiseBossDefeated(new SettlersBossDefeatedEvent(player, SettlersBossKind.BanditLord, AreaId, arena.Level));
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

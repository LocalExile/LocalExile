using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Modules.Settlers.Bosses;

/// <summary>
/// The Black Knight (<c>MonsterVarieties#10339</c>, <c>VerisiumGuardBoss</c>) in Starfall Crater
/// (<c>WorldAreas#1919</c>, <c>SettlersBossFallenStar</c>, level 83, <c>fallenstar/rooms/arena.arm</c>).
/// <para>Retail 3.29.3c encounter mechanics:</para>
/// <list type="bullet">
/// <item>4 invulnerable health phases at 80% (Summon Pillar Walls ring), 60% (Rockfall barrage + VerisiumGuardDaemon cracks),
/// 40% (Inner Pillar Walls + Laser Cover Pillars), and 20% (Heavy Rockfall barrage).</item>
/// <item>Full retail skill kit: Melee, Cold Cleave 1/2 (40% cold + 200% chill), Whirling Blades dash (40% cold + maim),
/// Crystalline Arc (<c>VerisiumCleaveProj</c> fan), Stalagmite Burst (<c>PizzaSlam</c>), Abyss Fissure (<c>AbyssSlam</c>),
/// Pizza Slice cone, Deflection Barrier (<c>azurite_shield=1</c>), and Verisium Laser (<c>GSKalguurGuardBeam</c>, 15% cold
/// penetration, blocked by and progressively cracking <c>VerisiumGuardBossPillarLaserPhase</c> cover pillars 1..31 -> shatter).</item>
/// <item>Post-kill: sets <c>SettlersKilledBlackKnight</c> + LeagueQuestFlag 1450 (unlocking Raulf's dialogue), retracts all
/// arena pillars, activates the 5 Verisium outcrops (1 main meteorite + 4 inner meteorites, <c>harvestable=1 -> 2</c>), invokes
/// <see cref="SettlersBossCallbacks.OnBossVerisiumClaimed"/>, and drops Svalinn (unique chance) + Gold + Runegraft chance.</item>
/// <item>Re-entry safe: Starfall Crater persists fight phase, boss life, and claimed Verisium outcrops in <c>InstanceState</c>.</item>
/// </list>
/// </summary>
public static class BlackKnightController
{
    public const string AreaId = "SettlersBossFallenStar";
    public const ushort AreaHash = 37119; // WorldAreas#1919
    public const int AreaLevel = 83;

    public const string BossVarietyId = "Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBoss"; // #10339
    public const string DaemonVarietyId = "Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardDaemon"; // #10363
    public const string PillarType = "Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBossPillar";
    public const string LaserPillarType = "Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBossPillarLaserPhase";
    public const string MainDepositType = "Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBossVerisiumDepositBase";
    public const string SmallDepositType = "Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBossVerisiumDepositSmall";

    public const string KilledQuestFlag = "SettlersKilledBlackKnight";
    public const int KilledLeagueQuestFlag = 1450; // Unlocks Raulf NPCTalk#3557 ("The Black Knight")

    // Verified GrantedEffects rows from client.db (MonsterVarieties#10339 & #10363).
    const int EffectMelee = 12539;
    const int EffectPizzaSlam = 12541;
    const int EffectAbyssSlam = 12542;
    const int EffectPizzaSlice = 12543;
    const int EffectProjectileShield = 12544;
    const int EffectVaalDash = 12545;
    const int EffectCleave1 = 12546;
    const int EffectCleave2 = 12547;
    const int EffectCleaveProj = 1899;
    const int EffectSummonPillars = 12551;
    const int EffectStartRage = 12554;
    const int EffectSummonRock = 7619;
    const int EffectBeam = 12555;
    const int EffectDaemonCracks = 7625;

    static readonly int[] BossEffects =
    [
        EffectMelee, EffectCleave1, EffectCleave2, EffectVaalDash, EffectCleaveProj,
        EffectPizzaSlam, EffectAbyssSlam, EffectPizzaSlice, EffectProjectileShield,
        EffectSummonPillars, EffectStartRage, EffectSummonRock, EffectBeam,
    ];

    // Verified .ao paths from data/game-files.zip.
    const string ArtRoot = "Metadata/Effects/Spells/monsters_effects/LeagueKalguur/VerisiumGuardBoss/ao/";
    const string ArtGroundSlam = ArtRoot + "groundslam/groundslam.ao";
    const string ArtGroundSlamBuildup = ArtRoot + "groundslam_detonation/buildup.ao";
    const string ArtGroundSlamDetonation = ArtRoot + "groundslam_detonation/groundslam_detonation.ao";
    const string ArtRockDrop = ArtRoot + "rockdrop/rock_drop.ao";
    const string ArtPillarBuildup = ArtRoot + "rockpillar/buildup.ao";
    const string ArtPillarExplode = ArtRoot + "rockpillar/rockpillar_explode.ao";
    const string ArtPillarIndicator = ArtRoot + "rockpillar/rockpillar_indicator_loop.ao";
    const string ArtLaserBeam = "Metadata/Effects/Spells/monsters_effects/League_Expedition/boss/olroth/relic_Scorching_beam.ao";
    const string ArtLaserDeflect = ArtRoot + "deflect/deflect_scorching_beam.ao";

    // Verified projectile from Projectiles.dat#1327.
    static readonly SkillCatalog.MonsterProjectileInfo CrystallineArcShot =
        new("Metadata/Projectiles/VerisiumCleaveProj", 1200, Lobbed: false, 3)
        {
            Count = 7,
            SpreadRadians = 1.25,
        };

    // Retail voice lines (NPCTextAudio, NPCs#681).
    static readonly string[] LinesEmerge = ["TheBlackKnight_Emerge_Random", "TheBlackKnight_Emerge_3", "TheBlackKnight_Emerge_5"];
    static readonly string[] LinesCleave = ["TheBlackKnight_Cleave_Random", "TheBlackKnight_Cleave_2"];
    static readonly string[] LinesDash = ["TheBlackKnight_Dash_Random", "TheBlackKnight_Dash_4"];
    static readonly string[] LinesDeflectStart = ["TheBlackKnight_Deflection_Start_Random", "TheBlackKnight_Deflection_Start_4"];
    static readonly string[] LinesDeflectEnd = ["TheBlackKnight_Deflection_End_Random", "TheBlackKnight_Deflection_End_4"];
    static readonly string[] LinesPillarSlam = ["TheBlackKnight_GroundSlam_Random", "TheBlackKnight_GroundSlam_4"];
    static readonly string[] LinesEpicSlamStart = ["TheBlackKnight_GroundSlam_Epic_Start_Random", "TheBlackKnight_GroundSlam_Epic_Start_3"];
    static readonly string[] LinesEpicSlamEnd = ["TheBlackKnight_GroundSlam_Epic_End_Random", "TheBlackKnight_GroundSlam_Epic_End_2"];
    static readonly string[] LinesRockfallStart = ["TheBlackKnight_Raise_Onto_Platform_Start_Random", "TheBlackKnight_Raise_Onto_Platform_Start_3"];
    static readonly string[] LinesRockfallEnd = ["TheBlackKnight_Raise_Onto_Platform_End_Random", "TheBlackKnight_Raise_Onto_Platform_End_3"];
    static readonly string[] LinesDeath = ["TheBlackKnight_Death_Random", "TheBlackKnight_Death_2", "TheBlackKnight_Death_3"];

    // Retail unique drop pool: Svalinn (Girded Tower Shield).
    public static readonly (string Name, int Weight)[] UniqueDropPool = [("Svalinn", 100)];
    public const double SvalinnDropChance = 0.15;

    // Health thresholds triggering invulnerable intermission phases.
    static readonly int[] PhaseThresholds = [80, 60, 40, 20];
    const string SaveKey = "Settlers/boss_black_knight";

    // Exact fallback coordinates from fallenstar/rooms/arena.arm (used when RoomObjects are absent).
    static readonly (int X, int Y) DefaultCentre = (416, 479);
    static readonly (int X, int Y) DefaultBossSpawn = (395, 515);
    const float DefaultBossRotation = 3.83972f;

    static readonly (string Type, string Model, int X, int Y, float Rot, bool IsMain)[] DefaultDeposits =
    [
        (MainDepositType, "Metadata/Terrain/Doodads/Leagues/Settlers/Meteorites/meteoriteMain01.ao", 385, 528, -2.53073f, true),
        (SmallDepositType, "Metadata/Terrain/Doodads/Leagues/Settlers/Meteorites/innermeteorites05.ao", 376, 433, -2.16421f, false),
        (SmallDepositType, "Metadata/Terrain/Doodads/Leagues/Settlers/Meteorites/innermeteorites04.ao", 471, 504, 0.488693f, false),
        (SmallDepositType, "Metadata/Terrain/Doodads/Leagues/Settlers/Meteorites/innermeteorites02.ao", 468, 455, -0.488693f, false),
        (SmallDepositType, "Metadata/Terrain/Doodads/Leagues/Settlers/Meteorites/innermeteorites03.ao", 353, 481, 3.05432f, false),
    ];

    sealed class DepositNode
    {
        public required int Index { get; init; }
        public required uint Id { get; init; }
        public required string Type { get; init; }
        public required string Model { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required float Rotation { get; init; }
        public required bool IsMain { get; init; }
        public int State; // 0 = dormant, 1 = harvestable, 2 = claimed
    }

    sealed class PillarObj
    {
        public required uint Id { get; init; }
        public required string Type { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required bool IsLaserCover { get; init; }
        public int Cracked;
        public bool Destroyed;
    }

    sealed class DelayedImpact
    {
        public required double HitAt { get; init; }
        public required (int X, int Y) Center { get; init; }
        public required double Radius { get; init; }
        public required MonsterAttack Attack { get; init; }
        public string? ImpactArt { get; init; }
        public float ImpactScale { get; init; } = 1f;
    }

    sealed class LaserState
    {
        public double EndsAt;
        public double NextTickAt;
        public double CurrentAngle;
        public uint BeamEffectId;
    }

    sealed class PersistedState
    {
        public int CompletedPhases { get; set; }
        public bool Defeated { get; set; }
        public HashSet<int> ClaimedDeposits { get; set; } = [];
    }

    enum IntermissionKind { None, PillarWall, Rockfall }

    sealed class ArenaState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public (int X, int Y) Centre = DefaultCentre;
        public uint BossId;
        public uint DaemonId;
        public int Level = AreaLevel;
        public bool Engaged;
        public bool Defeated;
        public int CompletedPhases;
        public IntermissionKind ActiveIntermission = IntermissionKind.None;
        public double IntermissionEndsAt;
        public double NextIntermissionWaveAt;
        public int IntermissionWaveCount;
        public bool DeflectActive;
        public double DeflectEndsAt;
        public LaserState? ActiveLaser;

        // Per-skill cooldowns (seconds).
        public double CdCleave;
        public double CdDash;
        public double CdArc;
        public double CdStalagmite;
        public double CdAbyss;
        public double CdLaser;
        public double CdDeflect;
        public int ComboStep;

        public readonly List<DepositNode> Deposits = [];
        public readonly List<PillarObj> Pillars = [];
        public readonly List<DelayedImpact> PendingImpacts = [];
        public readonly List<SettlersBosses.TimedEffect> Effects = [];
        public readonly HashSet<Player> PresentedPlayers = [];
    }

    static readonly ConditionalWeakTable<InstanceState, ArenaState> Arenas = new();

    internal static void ResetOnUnload() => Arenas.Clear();

    static ArenaState GetOrCreateArena(Player player) => Arenas.GetValue(player.Instance, inst =>
    {
        var state = new ArenaState { Level = Math.Max(AreaLevel, AreaLevelOf(player)) };
        if (inst.ContentState.TryGetValue(SaveKey, out string? json))
        {
            try
            {
                if (JsonSerializer.Deserialize<PersistedState>(json) is { } saved)
                {
                    state.CompletedPhases = saved.CompletedPhases;
                    state.Defeated = saved.Defeated;
                }
            }
            catch { }
        }
        BuildArenaLayout(player, state);
        return state;
    });

    static int AreaLevelOf(Player player) =>
        MapRunOf(player) is { Area.Level: > 0 } run ? run.Area.Level : AreaLevel;

    static void Persist(Player player, ArenaState arena)
    {
        var saved = new PersistedState
        {
            CompletedPhases = arena.CompletedPhases,
            Defeated = arena.Defeated,
            ClaimedDeposits = [.. arena.Deposits.Where(d => d.State == 2).Select(d => d.Index)],
        };
        player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(saved);
    }

    static void BuildArenaLayout(Player player, ArenaState arena)
    {
        var roomObjects = player.Layout.Level?.RoomObjects ?? [];
        var middle = roomObjects.FirstOrDefault(o => o.Type == SettlersBosses.ArenaMiddleType);
        arena.Centre = middle is not null ? (middle.X, middle.Y) : DefaultCentre;

        HashSet<int> claimed = [];
        if (player.Instance.ContentState.TryGetValue(SaveKey, out string? json))
        {
            try { if (JsonSerializer.Deserialize<PersistedState>(json) is { } s) claimed = s.ClaimedDeposits; }
            catch { }
        }

        // Collect all 5 Verisium outcrops from arena.arm RoomObjects, falling back to exact .arm defaults.
        var armDeposits = roomObjects
            .Where(o => o.Type is MainDepositType or SmallDepositType)
            .ToList();

        if (armDeposits.Count > 0)
        {
            for (int i = 0; i < armDeposits.Count; i++)
            {
                var o = armDeposits[i];
                bool isMain = o.Type == MainDepositType;
                int st = arena.Defeated ? (claimed.Contains(i) ? 2 : 1) : 0;
                arena.Deposits.Add(new DepositNode
                {
                    Index = i,
                    Id = player.Instance.World.NewMonsterId(),
                    Type = o.Type,
                    Model = string.IsNullOrEmpty(o.Model) ? DefaultDeposits[Math.Min(i, DefaultDeposits.Length - 1)].Model : o.Model,
                    X = o.X,
                    Y = o.Y,
                    Rotation = o.Rotation,
                    IsMain = isMain,
                    State = st,
                });
            }
        }
        else
        {
            for (int i = 0; i < DefaultDeposits.Length; i++)
            {
                var d = DefaultDeposits[i];
                int st = arena.Defeated ? (claimed.Contains(i) ? 2 : 1) : 0;
                arena.Deposits.Add(new DepositNode
                {
                    Index = i,
                    Id = player.Instance.World.NewMonsterId(),
                    Type = d.Type,
                    Model = d.Model,
                    X = d.X,
                    Y = d.Y,
                    Rotation = d.Rot,
                    IsMain = d.IsMain,
                    State = st,
                });
            }
        }
    }

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
                // Remove any auto-populated boss if already defeated in this instance.
                foreach (var (id, m) in player.Area.Monsters.ToArray())
                    if (m.Variety?.Id is BossVarietyId or DaemonVarietyId)
                        await SettlersBosses.DespawnMonster(player, id);
                return;
            }

            var spawned = await SettlersBosses.EnsureScriptedBoss(
                player, BossVarietyId, (byte)Math.Clamp(arena.Level, 1, 100),
                DefaultBossSpawn, DefaultBossRotation, BossEffects,
                shape: obj => obj with { TransitionState = 1 });
            if (spawned is { } b)
            {
                arena.BossId = b.Id;
                b.Monster.LifePhases = new LifePhaseGate(PhaseThresholds);
                for (int i = 0; i < arena.CompletedPhases && !b.Monster.LifePhases.Active; i++)
                {
                    if (b.Monster.LifePhases.Begin(1, 100))
                        b.Monster.LifePhases.Finish();
                }
            }

            // Spawn VerisiumGuardDaemon (10363) at arena center for rockfall ground crack bursts.
            if (arena.DaemonId == 0 && Monsters.Find(DaemonVarietyId) is { Supported: true } daemonVar)
            {
                var dSkills = SettlersBosses.BuildBossSkills(player, daemonVar, (byte)Math.Clamp(arena.Level, 1, 100), [EffectDaemonCracks]);
                arena.DaemonId = await SpawnMonster(player, daemonVar, (byte)Math.Clamp(arena.Level, 1, 100),
                    MonsterRarity.Normal, arena.Centre.X, arena.Centre.Y,
                    log: false, alone: false, unrisen: false, ownedSkills: dSkills,
                    scripted: true, nonBlocking: true, instanceOwned: true);
                if (arena.DaemonId != 0 && player.Area.Monsters.TryGetValue(arena.DaemonId, out var daemon))
                {
                    daemon.DamageImmune = true;
                    daemon.NoRewards = true;
                }
            }
        }
        finally { arena.Gate.Release(); }
    }

    internal static async Task OnPlayerEntered(Player player)
    {
        var arena = GetOrCreateArena(player);
        await PresentArenaObjects(player, arena);

        // On re-entry into an active fight, restore boss bar, LifePhaseGate, and sync scripted boss reference.
        if (!arena.Defeated && arena.BossId == 0)
        {
            foreach (var (id, m) in player.Area.Monsters.ToArray())
            {
                if (m.Variety?.Id == BossVarietyId && SettlersBosses.Alive(m))
                {
                    arena.BossId = id;
                    break;
                }
            }
        }
        if (!arena.Defeated && arena.BossId != 0 && player.Area.Monsters.TryGetValue(arena.BossId, out var boss))
        {
            if (boss.LifePhases is null)
            {
                boss.LifePhases = new LifePhaseGate(PhaseThresholds);
                int skip = arena.CompletedPhases;
                if (skip == 0 && boss.MaxLife > 0 && boss.Life > 0 && boss.Life < boss.MaxLife)
                {
                    double pct = 100.0 * boss.Life / boss.MaxLife;
                    for (int i = 0; i < PhaseThresholds.Length; i++)
                        if (pct < PhaseThresholds[i] - 1.0) skip = i + 1;
                }
                for (int i = 0; i < skip && !boss.LifePhases.Active; i++)
                    if (boss.LifePhases.Begin(1, 100)) boss.LifePhases.Finish();
            }
            if (arena.Engaged) await SettlersBosses.EnsureBossBar(player, arena.BossId, boss);
        }
    }

    static async Task PresentArenaObjects(Player player, ArenaState arena)
    {
        await arena.Gate.WaitAsync();
        try
        {
            if (!arena.PresentedPlayers.Add(player)) return;
            foreach (var node in arena.Deposits)
            {
                var sm = SettlersBosses.BaseStates(node.Type)?.Set("harvestable", node.State);
                await SettlersBosses.CreateQuestObj(player, node.Id, node.Type, node.X, node.Y, node.Rotation,
                    model: node.Model, states: sm, hideMinimap: node.State != 1);
            }
            foreach (var pillar in arena.Pillars)
            {
                if (pillar.Destroyed) continue;
                var sm = SettlersBosses.BaseStates(pillar.Type)?.Set("delete", 1);
                if (pillar.IsLaserCover && sm is not null) sm = sm.Set("cracked", pillar.Cracked);
                await SettlersBosses.CreateQuestObj(player, pillar.Id, pillar.Type, pillar.X, pillar.Y, 0f,
                    blocked: true, states: sm, hideMinimap: true);
                SettlersBosses.SetDiscBarrier(player, pillar.Id, pillar.X, pillar.Y, pillar.IsLaserCover ? 3 : 4, true);
            }
        }
        finally { arena.Gate.Release(); }
    }

    internal static async Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        if (!Arenas.TryGetValue(player.Instance, out var arena)) return;
        if (arena.Defeated && arena.Effects.Count == 0 && arena.PendingImpacts.Count == 0) return;

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

            // Process delayed slam / rockfall impacts even during transitions.
            await StepPendingImpacts(player, arena, boss, now);

            // Process active sweeping Verisium Laser beam.
            if (arena.ActiveLaser is not null)
                await StepLaser(player, arena, boss, now);

            // End deflection barrier if timed out.
            if (arena.DeflectActive && now >= arena.DeflectEndsAt)
            {
                arena.DeflectActive = false;
                SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, false);
                await SettlersBosses.SetBossStates(player, arena.BossId, ("azurite_shield", 0));
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesDeflectEnd);
            }

            if (!player.AiEnabled) return;

            var target = SettlersBosses.NearestLivingPlayer(player, boss.Walk.Position(now), now, maxRange: 180, awakeId: arena.BossId);
            if (target is null)
            {
                // Nobody alive in arena: lift intermission immunity so a returning player is never soft-locked.
                if (arena.ActiveIntermission != IntermissionKind.None)
                    await EndIntermission(player, arena, boss);
                return;
            }

            // Engage on first proximity.
            if (!arena.Engaged)
            {
                if (!SettlersBosses.Near(target.Walk.Position(now), boss.Walk.Position(now), 95)) return;
                arena.Engaged = true;
                boss.Aggro = true;
                await SettlersBosses.SetBossTransition(player, arena.BossId, 2);
                await SettlersBosses.SetBossStates(player, arena.BossId, ("boss_life_bar", 1), ("boss_lights", 1), ("global_first_aggro", 1));
                await SettlersBosses.EnsureBossBar(player, arena.BossId, boss);
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesEmerge);
                boss.BusyUntil = now + 1.4;
                return;
            }

            // Check health phase thresholds (80%, 60%, 40%, 20%).
            if (arena.ActiveIntermission == IntermissionKind.None
                && boss.LifePhases is { Active: false } phases
                && phases.Begin(boss.Life, boss.MaxLife))
            {
                await StartIntermission(player, arena, boss, now);
                return;
            }

            if (arena.ActiveIntermission != IntermissionKind.None)
            {
                await StepIntermission(player, arena, boss, target, now);
                return;
            }

            if (now < boss.BusyUntil || arena.ActiveLaser is not null) return;
            await ActCombat(player, arena, boss, target, now);
        }
        finally { arena.Gate.Release(); }
    }

    static async Task StartIntermission(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        int phaseIndex = arena.CompletedPhases; // 0 = 80% pillars, 1 = 60% rockfall, 2 = 40% pillars, 3 = 20% rockfall
        bool isPillarPhase = phaseIndex % 2 == 0;
        arena.ActiveIntermission = isPillarPhase ? IntermissionKind.PillarWall : IntermissionKind.Rockfall;
        arena.IntermissionWaveCount = 0;

        // Clear any active deflection shield before phase transition.
        if (arena.DeflectActive)
        {
            arena.DeflectActive = false;
            await SettlersBosses.SetBossStates(player, arena.BossId, ("azurite_shield", 0));
        }

        SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, true);
        await SettlersBosses.TeleportBoss(player, arena.BossId, boss, arena.Centre, now);

        if (isPillarPhase)
        {
            arena.IntermissionEndsAt = now + 5.5;
            arena.NextIntermissionWaveAt = now + 1.0;
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectSummonPillars, arena.Centre, now, 2.2);
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesPillarSlam);
            // Spawn shrinking ring of stone pillar walls + laser cover pillars.
            double ringRadius = phaseIndex == 0 ? 52.0 : 38.0;
            int count = phaseIndex == 0 ? 12 : 10;
            await SpawnPillarRing(player, arena, ringRadius, count, isLaserCover: false);
            await SpawnPillarRing(player, arena, 24.0, 4, isLaserCover: true);
        }
        else
        {
            arena.IntermissionEndsAt = now + 8.0;
            arena.NextIntermissionWaveAt = now + 1.1;
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectStartRage, arena.Centre, now, 2.0);
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesRockfallStart);
        }
    }

    static async Task StepIntermission(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        if (now >= arena.IntermissionEndsAt)
        {
            await EndIntermission(player, arena, boss);
            if (arena.CompletedPhases % 2 == 0)
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesRockfallEnd);
            return;
        }

        if (now < arena.NextIntermissionWaveAt) return;

        if (arena.ActiveIntermission == IntermissionKind.PillarWall)
        {
            // During Pillar Wall phase, pulse a sweeping Verisium Laser so players shelter behind the cover pillars!
            arena.NextIntermissionWaveAt = arena.IntermissionEndsAt + 1.0;
            await BeginLaser(player, arena, boss, target, now, duration: 3.6);
        }
        else if (arena.ActiveIntermission == IntermissionKind.Rockfall)
        {
            arena.IntermissionWaveCount++;
            arena.NextIntermissionWaveAt = now + 0.85;
            await TriggerRockfallWave(player, arena, boss, target, now, heavy: arena.CompletedPhases >= 3);
        }
    }

    static async Task EndIntermission(Player player, ArenaState arena, SpawnedMonster boss)
    {
        arena.ActiveIntermission = IntermissionKind.None;
        arena.CompletedPhases++;
        SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, false);
        boss.LifePhases?.Finish();
        Persist(player, arena);
    }

    static async Task SpawnPillarRing(Player player, ArenaState arena, double radius, int count, bool isLaserCover)
    {
        string type = isLaserCover ? LaserPillarType : PillarType;
        var rng = AreaRng(player);
        double offset = rng.NextDouble() * Math.Tau;
        for (int i = 0; i < count; i++)
        {
            double angle = offset + i * Math.Tau / count;
            int px = arena.Centre.X + (int)Math.Round(Math.Cos(angle) * radius);
            int py = arena.Centre.Y + (int)Math.Round(Math.Sin(angle) * radius);
            if (player.Layout.Walkable is { } walk && !walk(px, py)) continue;
            // Avoid overlapping existing active pillars.
            if (arena.Pillars.Any(p => !p.Destroyed && SettlersBosses.Near((p.X, p.Y), (px, py), 6))) continue;

            uint pid = player.Instance.World.NewMonsterId();
            var pillar = new PillarObj { Id = pid, Type = type, X = px, Y = py, IsLaserCover = isLaserCover };
            arena.Pillars.Add(pillar);

            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, (px, py), ArtPillarBuildup, AreaTime(player) + 1.2);
            var sm = SettlersBosses.BaseStates(type)?.Set("delete", 1);
            foreach (var member in SettlersBosses.ActivePlayers(player))
            {
                await SettlersBosses.CreateQuestObj(member, pid, type, px, py, (float)angle, blocked: true, states: sm, hideMinimap: true);
                SettlersBosses.SetDiscBarrier(member, pid, px, py, isLaserCover ? 3 : 4, true);
            }
        }
    }

    static async Task TriggerRockfallWave(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now, bool heavy)
    {
        var rng = AreaRng(player);
        int rocks = heavy ? 8 : 5;
        var rockAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectSummonRock,
            scale: heavy ? 1.35 : 1.1, range: 18, physToColdPct: 30, forceSpell: true);

        // Fire VerisiumGuardDaemon ground cracks visual if daemon is present.
        if (arena.DaemonId != 0 && player.Area.Monsters.TryGetValue(arena.DaemonId, out var daemon))
            await SettlersBosses.PlayBossSkill(player, arena.DaemonId, daemon, [EffectDaemonCracks], EffectDaemonCracks, target.Walk.Position(now), now, 0.5);

        var tPos = target.Walk.Position(now);
        for (int i = 0; i < rocks; i++)
        {
            var dropAt = i == 0
                ? tPos
                : SettlersBosses.WalkableNear(player, (
                    arena.Centre.X + rng.Next(-42, 43),
                    arena.Centre.Y + rng.Next(-42, 43)));
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, dropAt, ArtPillarIndicator, now + 1.15);
            arena.PendingImpacts.Add(new DelayedImpact
            {
                HitAt = now + 1.15,
                Center = dropAt,
                Radius = 12,
                Attack = rockAtk,
                ImpactArt = ArtRockDrop,
                ImpactScale = 1.1f,
            });
        }
    }

    static async Task ActCombat(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now)
    {
        var bPos = boss.Walk.Position(now);
        var tPos = target.Walk.Position(now);
        double dist = SettlersBosses.Distance(bPos, tPos);
        double facing = Math.Atan2(tPos.Y - bPos.Y, tPos.X - bPos.X);

        // 1. Verisium Laser (cooldown 16s, mid/long range, after phase 1 or random).
        if (now >= arena.CdLaser && dist >= 16 && dist <= 75)
        {
            arena.CdLaser = now + 16.0;
            await BeginLaser(player, arena, boss, target, now, duration: 3.0);
            return;
        }

        // 2. Deflection Barrier (azurite_shield=1, cooldown 24s).
        if (now >= arena.CdDeflect && arena.CompletedPhases >= 1)
        {
            arena.CdDeflect = now + 26.0;
            arena.DeflectActive = true;
            arena.DeflectEndsAt = now + 2.4;
            SettlersBosses.SetBossInvulnerable(player, arena.BossId, boss, true);
            await SettlersBosses.SetBossStates(player, arena.BossId, ("azurite_shield", 1));
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectProjectileShield, tPos, now, 2.4);
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesDeflectStart);
            return;
        }

        // 3. Stalagmite Burst (PizzaSlam, 20 spikes across arena, cooldown 14s).
        if (now >= arena.CdStalagmite && dist <= 55)
        {
            arena.CdStalagmite = now + 14.0;
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesEpicSlamStart);
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectPizzaSlam, tPos, now, 2.6);
            var slamAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectPizzaSlam,
                scale: 1.55, range: 14, physToColdPct: 40);
            var rng = AreaRng(player);
            // 12 radial spike bursts radiating outward from boss + around target.
            for (int i = 0; i < 12; i++)
            {
                double a = facing + (i - 6) * 0.45 + (rng.NextDouble() - 0.5) * 0.2;
                double r = 10 + (i % 4) * 10;
                var p = SettlersBosses.WalkableNear(player, (bPos.X + (int)Math.Round(Math.Cos(a) * r), bPos.Y + (int)Math.Round(Math.Sin(a) * r)));
                await SettlersBosses.SpawnTimedEffect(player, arena.Effects, p, ArtGroundSlamBuildup, now + 1.3 + (i % 3) * 0.25);
                arena.PendingImpacts.Add(new DelayedImpact
                {
                    HitAt = now + 1.3 + (i % 3) * 0.25,
                    Center = p,
                    Radius = 10,
                    Attack = slamAtk,
                    ImpactArt = ArtGroundSlamDetonation,
                });
            }
            return;
        }

        // 4. Crystalline Arc (fan of piercing VerisiumCleaveProj projectiles, cooldown 8s).
        if (now >= arena.CdArc && dist >= 14 && dist <= 75)
        {
            arena.CdArc = now + 8.0;
            var arcAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectCleaveProj,
                scale: 1.05, range: 70, physToColdPct: 25, forceSpell: true, projectile: CrystallineArcShot);
            SettlersBosses.LaunchBossSwing(player, arena.BossId, boss, arcAtk, now, recoverySeconds: 0.4);
            return;
        }

        // 5. Whirling Blades gap-closer (VaalDash, 40% cold + maim, cooldown 6s).
        if (now >= arena.CdDash && dist >= 22 && dist <= 70)
        {
            arena.CdDash = now + 6.5;
            await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesDash);
            var dashDest = SettlersBosses.WalkableNear(player, SettlersBosses.StepToward(bPos, tPos, Math.Max(14, dist - 6)));
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectVaalDash, dashDest, now, 0.9);
            var dashAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectVaalDash,
                scale: 1.25, range: 14, physToColdPct: 40);
            await SettlersBosses.TeleportBoss(player, arena.BossId, boss, dashDest, now);
            await SettlersBosses.HitSegment(player, arena.BossId, boss, dashAtk, bPos, dashDest, halfWidth: 9,
                () => !arena.Defeated && SettlersBosses.Alive(boss));
            return;
        }

        // 6. Abyss Fissure line slam (cooldown 12s).
        if (now >= arena.CdAbyss && dist <= 45)
        {
            arena.CdAbyss = now + 12.0;
            await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectAbyssSlam, tPos, now, 1.8);
            var abyssAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectAbyssSlam,
                scale: 1.35, range: 12, physToColdPct: 75);
            for (int step = 1; step <= 4; step++)
            {
                var p = SettlersBosses.WalkableNear(player, (
                    bPos.X + (int)Math.Round(Math.Cos(facing) * step * 11),
                    bPos.Y + (int)Math.Round(Math.Sin(facing) * step * 11)));
                await SettlersBosses.SpawnTimedEffect(player, arena.Effects, p, ArtGroundSlam, now + 0.65 + step * 0.22);
                arena.PendingImpacts.Add(new DelayedImpact
                {
                    HitAt = now + 0.65 + step * 0.22,
                    Center = p,
                    Radius = 9,
                    Attack = abyssAtk,
                });
            }
            return;
        }

        // 7. Close range: alternating Cold Cleave (1/2) and Default Melee.
        if (dist <= 18)
        {
            if (now >= arena.CdCleave)
            {
                arena.CdCleave = now + 4.0;
                int eff = (arena.ComboStep++ % 2 == 0) ? EffectCleave1 : EffectCleave2;
                await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesCleave);
                await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, eff, tPos, now, 1.1);
                var cleaveAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, eff,
                    scale: 1.35, range: 20, physToColdPct: 40);
                await SettlersBosses.HitCone(player, arena.BossId, boss, cleaveAtk, bPos, facing, halfAngleRad: 0.85, maxRange: 20,
                    () => !arena.Defeated && SettlersBosses.Alive(boss));
                return;
            }
            var meleeAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectMelee, scale: 1.0, range: 15);
            SettlersBosses.LaunchBossSwing(player, arena.BossId, boss, meleeAtk, now, recoverySeconds: 0.25);
            return;
        }

        // Otherwise close distance cleanly.
        await SettlersBosses.MoveBoss(player, arena.BossId, boss, tPos, now);
    }

    static async Task BeginLaser(Player player, ArenaState arena, SpawnedMonster boss, Player target, double now, double duration)
    {
        var bPos = boss.Walk.Position(now);
        var tPos = target.Walk.Position(now);
        double angle = Math.Atan2(tPos.Y - bPos.Y, tPos.X - bPos.X);
        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesEpicSlamEnd);
        await SettlersBosses.PlayBossSkill(player, arena.BossId, boss, BossEffects, EffectBeam, tPos, now, duration);
        uint fxId = await SettlersBosses.SpawnTimedEffect(player, arena.Effects, bPos, ArtLaserBeam, now + duration, scale: 1.2f, rotation: (float)angle);
        arena.ActiveLaser = new LaserState
        {
            EndsAt = now + duration,
            NextTickAt = now + 0.35,
            CurrentAngle = angle,
            BeamEffectId = fxId,
        };
    }

    static async Task StepLaser(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        var laser = arena.ActiveLaser!;
        if (now >= laser.EndsAt)
        {
            arena.ActiveLaser = null;
            return;
        }
        if (now < laser.NextTickAt) return;
        laser.NextTickAt = now + 0.25;

        var bPos = boss.Walk.Position(now);
        var target = SettlersBosses.NearestLivingPlayer(player, bPos, now, 160);
        if (target is not null)
        {
            var tPos = target.Walk.Position(now);
            double desired = Math.Atan2(tPos.Y - bPos.Y, tPos.X - bPos.X);
            double delta = Math.IEEERemainder(desired - laser.CurrentAngle, Math.Tau);
            // Smooth sweep speed so players can kite behind cover pillars.
            laser.CurrentAngle += Math.Clamp(delta, -0.18, 0.18);
        }

        const double maxLen = 78.0;
        double effectiveLen = maxLen;
        double cos = Math.Cos(laser.CurrentAngle), sin = Math.Sin(laser.CurrentAngle);

        // Check if any intact VerisiumGuardBossPillarLaserPhase pillar intercepts the beam!
        PillarObj? hitPillar = null;
        foreach (var p in arena.Pillars)
        {
            if (p.Destroyed) continue;
            double dx = p.X - bPos.X, dy = p.Y - bPos.Y;
            double proj = dx * cos + dy * sin;
            if (proj <= 2.0 || proj >= effectiveLen) continue;
            double perp = Math.Abs(-dx * sin + dy * cos);
            if (perp <= 5.5)
            {
                effectiveLen = proj;
                hitPillar = p;
            }
        }

        if (hitPillar is not null)
        {
            await SettlersBosses.SpawnTimedEffect(player, arena.Effects, (hitPillar.X, hitPillar.Y), ArtLaserDeflect, now + 0.35);
            if (hitPillar.IsLaserCover)
            {
                // Crack cover pillar progressively (verisiumguardbosspillarlaserphase.ot: 9/18/27 damaged, 31 shatter!).
                hitPillar.Cracked = Math.Min(31, hitPillar.Cracked + 4);
                var sm = SettlersBosses.BaseStates(hitPillar.Type)?.Set("delete", 1).Set("cracked", hitPillar.Cracked);
                if (sm is not null) await SettlersBosses.UpdateObjStates(player, hitPillar.Id, hitPillar.Type, sm);
                if (hitPillar.Cracked >= 31)
                {
                    hitPillar.Destroyed = true;
                    await SettlersBosses.SpawnTimedEffect(player, arena.Effects, (hitPillar.X, hitPillar.Y), ArtPillarExplode, now + 1.0);
                    foreach (var member in SettlersBosses.ActivePlayers(player))
                    {
                        SettlersBosses.SetDiscBarrier(member, hitPillar.Id, hitPillar.X, hitPillar.Y, 3, false);
                        await SendWorldRemoval(PlayerOf(member), hitPillar.Id, null);
                    }
                }
            }
        }

        var endPos = (bPos.X + (int)Math.Round(cos * effectiveLen), bPos.Y + (int)Math.Round(sin * effectiveLen));
        // Verisium Laser deals cold damage (with 15% cold pen modeled via +15% cold scale).
        var beamAtk = SettlersBosses.BuildBossAttack(player, boss, BossEffects, EffectBeam,
            scale: 0.65, range: maxLen, physToColdPct: 100, forceSpell: true);
        await SettlersBosses.HitSegment(player, arena.BossId, boss, beamAtk, bPos, endPos, halfWidth: 5.5,
            () => !arena.Defeated && SettlersBosses.Alive(boss));
    }

    static async Task StepPendingImpacts(Player player, ArenaState arena, SpawnedMonster boss, double now)
    {
        for (int i = arena.PendingImpacts.Count - 1; i >= 0; i--)
        {
            var imp = arena.PendingImpacts[i];
            if (now < imp.HitAt) continue;
            arena.PendingImpacts.RemoveAt(i);
            if (imp.ImpactArt is not null)
                await SettlersBosses.SpawnTimedEffect(player, arena.Effects, imp.Center, imp.ImpactArt, now + 1.1, imp.ImpactScale);
            await SettlersBosses.HitCircle(player, arena.BossId, boss, imp.Attack, imp.Center, imp.Radius,
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
        arena.ActiveIntermission = IntermissionKind.None;
        arena.ActiveLaser = null;
        arena.PendingImpacts.Clear();

        await SettlersBosses.Speak(player, arena.BossId, SettlersBosses.BlackKnightNpcRow, LinesDeath);
        await SettlersBosses.ClearEffects(player, arena.Effects);

        // Retract and remove all spawned stone pillars & clear walk barriers.
        foreach (var pillar in arena.Pillars)
        {
            if (pillar.Destroyed) continue;
            pillar.Destroyed = true;
            var retract = SettlersBosses.BaseStates(pillar.Type)?.Set("delete", 2);
            if (retract is not null) await SettlersBosses.UpdateObjStates(player, pillar.Id, pillar.Type, retract);
            foreach (var member in SettlersBosses.ActivePlayers(player))
                SettlersBosses.SetDiscBarrier(member, pillar.Id, pillar.X, pillar.Y, pillar.IsLaserCover ? 3 : 4, false);
        }

        // Despawn helper daemon cleanly.
        if (arena.DaemonId != 0)
        {
            await SettlersBosses.DespawnMonster(player, arena.DaemonId);
            arena.DaemonId = 0;
        }

        // Unlock all 5 BossVerisium outcrops (harvestable=1 -> targetable + OreTypeVerisium minimap icon + resource_collect.epk!).
        foreach (var node in arena.Deposits)
        {
            if (node.State == 2) continue;
            node.State = 1;
            var sm = SettlersBosses.BaseStates(node.Type)?.Set("harvestable", 1);
            if (sm is not null) await SettlersBosses.UpdateObjStates(player, node.Id, node.Type, sm);
        }

        Persist(player, arena);

        // Set QuestFlag SettlersKilledBlackKnight + LeagueQuestFlag 1450 (Raulf dialogue).
        await SettlersBosses.GrantBossKillFlags(player, KilledQuestFlag, KilledLeagueQuestFlag);

        // Drop boss loot: Gold + Svalinn unique chance + Runegraft chance.
        var dropAt = player.Layout.Near(killAt);
        await SettlersBosses.DropBossGold(player, dropAt, minGold: 8000, maxGold: 15000, ownerOnly: false);
        await SettlersBosses.TryDropBossUnique(player, dropAt, arena.Level, UniqueDropPool, SvalinnDropChance, scatterIndex: 13);
        if (AreaRng(player).NextDouble() < 0.35)
            await SettlersBosses.DropRunegrafts(player, dropAt, arena.Level, 1, 1, scatterBase: 15);

        await SettlersBossCallbacks.RaiseBossDefeated(new SettlersBossDefeatedEvent(player, SettlersBossKind.BlackKnight, AreaId, arena.Level));
    }

    internal static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Arenas.TryGetValue(player.Instance, out var arena)) return false;
        DepositNode? node = null;
        await arena.Gate.WaitAsync();
        try { node = arena.Deposits.FirstOrDefault(d => d.Id == use.TargetId); }
        finally { arena.Gate.Release(); }
        if (node is null) return false;

        if (!arena.Defeated || node.State != 1 || player.PlayerLife == 0)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }

        const int reach = 14;
        if (!TryBeginObjectWalk(player, requestedAt, node.X, node.Y, reach, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, node.X, node.Y,
                    use.SkillId, use.Sequence, use.Flags, targetObjectId: node.Id),
                null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);

        bool claimedNow = false;
        int claimedCount = 0;
        int totalCount = 0;
        if (PlayerActionCurrent(player, action) && player.PlayerLife > 0 && PlayerWalk.InWalkUpRange(stop, (node.X, node.Y), reach))
        {
            await arena.Gate.WaitAsync();
            try
            {
                if (node.State == 1)
                {
                    node.State = 2;
                    claimedNow = true;
                    claimedCount = arena.Deposits.Count(d => d.State == 2);
                    totalCount = arena.Deposits.Count;
                    Persist(player, arena);
                    var sm = SettlersBosses.BaseStates(node.Type)?.Set("harvestable", 2);
                    if (sm is not null) await SettlersBosses.UpdateObjStates(player, node.Id, node.Type, sm);
                }
            }
            finally { arena.Gate.Release(); }
        }

        if (claimedNow)
        {
            // Retail Starfall Crater Verisium outcrop yield: large central meteorite gives ~450-650, each of 4 inner meteorites gives ~200-320.
            var rng = AreaRng(player);
            int baseOre = node.IsMain ? rng.Next(450, 651) : rng.Next(200, 321);
            int amount = Math.Max(1, (int)Math.Round(baseOre * SettlersBossCallbacks.OreFoundMultiplier()));
            await SettlersBossCallbacks.RaiseBossVerisiumClaimed(
                new BossVerisiumClaimedEvent(player, amount, node.IsMain, arena.Level, claimedCount, totalCount));
        }

        await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
        return true;
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

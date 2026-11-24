using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 10 — The Control Blocks (<c>2_10_7</c>) Vilenta (<c>Vilenta[6686]</c>) encounter controller:
/// <list type="bullet">
/// <item><b><c>[66, 33]</c> Aerial Carpet-Bomb Intermissions</b>: Casts <c>VilentaHide[9402]</c> (untargetable
/// <c>WorldHidden</c> + <c>SetInvulnerable</c>), sweeps fire/gas lanes across the arena using
/// <c>VilentaCarpetBombDaemon_</c> / <c>VilentaGasMortarMulti[8906]</c>, and spawns <c>Test Subject</c>
/// (<c>OriathanCitizenZombie*VilentaMinion</c>) &amp; <c>Kiln Crawler</c> (<c>SkeletonMeleeFireVilenta*</c>) adds
/// (<c>NoRewards = true</c>).</item>
/// <item><b>Sub-50% Enrage Stance</b>: Casts <c>VilentaChangeToStance2[6203]</c> entering <c>StanceVilentaEnrage</c>
/// (<c>enrage_buff[684]</c> + boosted attack/move speed + <c>VilentaEnrageMelee[484]</c>).</item>
/// <item><b>Active Combat &amp; Doors</b>: <c>VilentaDash[357]</c>, directional geometry slashes (<c>9083/9085</c>),
/// <c>VilentaXMortar[6388]</c>, <c>VilentaSunder[5637]</c>, and <c>GenericDoorToggle</c>/<c>GenericDoorToggleOpen</c>
/// arena door gating.</item>
/// </list>
/// </summary>
public static class VilentaEncounter
{
    public const string Area = "2_10_7";
    public const string Vilenta = "Metadata/Monsters/Vilenta/Vilenta";                                          // [6686]
    public const string TestSubjectMale = "Metadata/Monsters/OriathanCitizen/OriathanCitizenZombieMaleVilentaMinion";     // [6693]
    public const string TestSubjectFemale = "Metadata/Monsters/OriathanCitizen/OriathanCitizenZombieFemaleVilentaMinion"; // [6694]
    public const string KilnCrawlerAxe = "Metadata/Monsters/Skeletons/SkeletonMeleeFireVilentaAxe";             // [823]
    public const string KilnCrawlerSword = "Metadata/Monsters/Skeletons/SkeletonMeleeFireVilentaSword";         // [824]

    public const string GenericDoorToggleType = "Metadata/MiscellaneousObjects/GenericDoorToggle";
    public const string GenericDoorToggleOpenType = "Metadata/MiscellaneousObjects/GenericDoorToggleOpen";

    // Verified GrantedEffects rows on Vilenta[6686] in client.db:
    const int MeleeEffect = 406;
    const int GasMortarEffect = 8904;        // VilentaGasMortar
    const int HideEffect = 9402;             // VilentaHide
    const int DashEffect = 357;              // VilentaDash
    const int GeometryLeftEffect = 9083;     // VilentaGeometryAttackLeft
    const int GeometryRightEffect = 9085;    // VilentaGeometryAttackRight
    const int SlamEffect = 5603;             // VilentaSlam
    const int XMortarEffect = 6388;          // VilentaXMortar
    const int SunderEffect = 5637;           // VilentaSunder
    const int GasMortarMultiEffect = 8906;   // VilentaGasMortarMulti
    const int EnrageMeleeEffect = 484;       // VilentaEnrageMelee
    const int ChangeToStance2Effect = 6203;  // VilentaChangeToStance2

    static readonly int[] VilentaEffects =
    [
        MeleeEffect, GasMortarEffect, HideEffect, DashEffect,
        GeometryLeftEffect, GeometryRightEffect, SlamEffect,
        XMortarEffect, SunderEffect, GasMortarMultiEffect,
        EnrageMeleeEffect, ChangeToStance2Effect,
    ];

    // Verified BuffDefinitions rows in client.db:
    const ushort CannotBeDamagedBuff = 350;  // cannot_be_damaged
    const ushort EnrageBuff = 684;           // enrage_buff
    const ushort CausticCloudBuff = 484;     // caustic_cloud

    // Verified .ao visual paths in game-files.zip:
    const string BombingAoeArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/vilentabombingaoe.ao";
    const string GasMortarExplodeArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/gasmortar/vilgasmortarexplode.ao";
    const string GasCloudArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/gasmortar/vilgasmortargas.ao";
    const string GroundSlamArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/vilentagroundslam.ao";
    const string MortarArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/vilentamortar.ao";
    const string ComboExplodeArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/vilcombomortarexplode.ao";
    const string RageBeamArt = "Metadata/Effects/Spells/monsters_effects/act10/vilenta/vilentaragebeam.ao";

    const string PhaseKey = "Vilenta";
    static readonly int[] PhaseThresholds = [66, 33];

    static readonly IReadOnlyDictionary<uint, int> EnrageStats = new Dictionary<uint, int>
    {
        [Stat.AttackSpeedPlusPct] = 30,
        [Stat.BaseMovementVelocityPlusPct] = 25,
    };

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id == Vilenta);
            if (!present) return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player));
        }
        await fight.Gate.WaitAsync();
        try
        {
            SyncGatesForPlayer(player, fight.Gates);
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static async Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return;
        if (Fights.TryGetValue(player.Instance, out var fight))
        {
            ReleaseGatesForPlayer(player, fight.Gates);
            await fight.ReleasePlayerDebuffs(player);
        }
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            ReleaseAllGatesOnUnload();
        }
    }

    sealed record GasPool((int X, int Y) At, double Radius, double Until);
    sealed record DelayedAttack(double At, uint SourceId, SpawnedMonster Source, MonsterAttack Attack,
        (int X, int Y) From, (int X, int Y) To, double Facing, double Radius, AttackShape Shape,
        string? BurstAo, bool LeavesGas = false);
    enum AttackShape { Circle, Cone, DashLine }

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<ArenaGate> Gates = [];

        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<GasPool> gasPools = [];
        readonly List<DelayedAttack> attacks = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> waveAdds = [];
        readonly Dictionary<Player, ArenaHazards.Exposure> gasExposures = [];
        readonly object gasHolder = new();

        readonly List<(int X, int Y)> zombieSpawners;
        readonly List<(int X, int Y)> carpetBombAnchors;
        readonly List<(string Type, GlyphQuest.Placement Placement)> doorPlacements;

        (int X, int Y) arenaCenter;
        uint bossId;
        bool gatesInit, gatesLocked, initialized, airborne, enraged, cleaned, stopped;
        double airborneEndsAt, nextCarpetLaneAt, nextSpecial, nextGasTick;
        int carpetLaneIndex, rotation;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            zombieSpawners = [.. placed.Of("Metadata/Monsters/Vilenta/VilentaZombieSpawner").Select(p => (p.X, p.Y))];
            carpetBombAnchors = [.. placed.Of("Metadata/Monsters/Daemon/VilentaCarpetBombDaemon_")
                .Concat(placed.Of("Metadata/Monsters/AtlasBosses/TheElder/ArenaObjects/Tower/BeamTarget1"))
                .Concat(placed.Of("Metadata/Monsters/AtlasBosses/TheElder/ArenaObjects/Tower/BeamTarget2"))
                .Concat(placed.Of("Metadata/Monsters/AtlasBosses/TheElder/ArenaObjects/Tower/BeamTarget3"))
                .Concat(placed.Of("Metadata/Monsters/AtlasBosses/TheElder/ArenaObjects/Tower/BeamTarget4"))
                .Select(p => (p.X, p.Y))];
            doorPlacements =
            [
                .. placed.Of(GenericDoorToggleType).Select(p => (GenericDoorToggleType, p)),
                .. placed.Of(GenericDoorToggleOpenType).Select(p => (GenericDoorToggleOpenType, p)),
            ];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bossId != 0 && area.Monsters.TryGetValue(bossId, out var b))
            {
                ClearInvulnerableOnUnload(area, bossId, b, CannotBeDamagedBuff);
                b.RemoveTemporaryStats("vilenta_enrage");
                b.RebuildStats();
                area.Buffs.Clear(bossId, EnrageBuff, bossId);
                b.WorldHidden = false;
            }
            foreach (var p in gasExposures.Keys.ToArray())
                _ = ReleasePlayerDebuff(p, gasHolder);
            gasExposures.Clear();
        }

        public async Task ReleasePlayerDebuffs(Player p)
        {
            gasExposures.Remove(p);
            await ReleasePlayerDebuff(p, gasHolder);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            gasPools.RemoveAll(g => now >= g.Until);
            await StepDelayedAttacks(runner, now);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Vilenta);
            if (entry.Value is not { } boss) return;
            bool rebound = bossId != entry.Key;
            bossId = entry.Key;

            if (!gatesInit)
            {
                gatesInit = true;
                var pos0 = boss.Walk.Position(now);
                foreach (var (type, dp) in doorPlacements)
                {
                    if (Distance(pos0, (dp.X, dp.Y)) > 140) continue;
                    if (await CreateGate(runner, type, dp, startLocked: false, lockedState: 2, unlockedState: 1) is { } g)
                        Gates.Add(g);
                }
            }

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, VilentaEffects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);
            var phases = boss.LifePhases!;
            var pos = boss.Walk.Position(now);

            if (!initialized || rebound)
            {
                if (!initialized)
                {
                    initialized = true;
                    arenaCenter = pos;
                    nextSpecial = now + 2.2;
                }
                if (airborne)
                {
                    SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                    await SetWorldHidden(runner, bossId, boss, true);
                }
                Console.WriteLine($"[gateway] Vilenta controller synced for 0x{bossId:x} (intermissions done={phases.Completed}/2)");
            }

            var target = NearestLivingTarget(runner, airborne ? arenaCenter : pos, now, maxRange: 140, awakeId: airborne ? null : bossId);
            if (target is null)
            {
                if (gatesLocked)
                {
                    gatesLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                }
                return;
            }

            if (!gatesLocked)
            {
                gatesLocked = true;
                foreach (var g in Gates) await SetGateLocked(runner, g, true);
            }

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;
            MarkStarted(runner.Instance, PhaseKey);

            await StepGasPools(runner, bossId, boss, now);

            // Active [66, 33] aerial carpet-bomb intermission.
            if (airborne)
            {
                await StepAirborneIntermission(runner, bossId, boss, target, now);
                return;
            }

            // Safety guarantee: never leave Vilenta invulnerable or hidden outside an intermission.
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            if (boss.WorldHidden)
                await SetWorldHidden(runner, bossId, boss, false);

            // Trigger [66, 33] aerial carpet-bomb intermission.
            if (phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                await BeginAirborneIntermission(runner, bossId, boss, phases.Completed, now);
                return;
            }

            // Sub-50% Enrage Stance (VilentaChangeToStance2[6203] -> StanceVilentaEnrage).
            if (!enraged && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 50)
            {
                enraged = true;
                await CastAction(runner, bossId, boss, VilentaEffects, ChangeToStance2Effect, pos, now, DamageType.Fire, 0.5, 16, "Vilenta");
                boss.SetTemporaryStats("vilenta_enrage", EnrageStats);
                boss.RebuildStats();
                runner.Area.Buffs.Set(bossId, new(EnrageBuff, bossId));
                if (runner.Instance.World.MonsterOrigins.TryGetValue(bossId, out var origin))
                    runner.Instance.World.MonsterOrigins[bossId] = origin with { Object = origin.Object with { ActorStance = 1 } };
                await SpawnTimedEffect(runner, effects, pos, RageBeamArt, now + 2.5, scale: 1.3f);
                Console.WriteLine($"[gateway] Vilenta 0x{bossId:x} entered StanceVilentaEnrage below 50% HP");
                return;
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            if (now >= nextSpecial)
            {
                int step = rotation++;
                nextSpecial = now + (enraged ? 3.2 : 4.2);

                if (step % 5 == 0)
                {
                    // VilentaDash[357]: rapid line dash across target.
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var dashEnd = WalkableNear(runner, (
                        (int)Math.Round(aim.X + Math.Cos(angle) * 10),
                        (int)Math.Round(aim.Y + Math.Sin(angle) * 10)));
                    if (await CastAction(runner, bossId, boss, VilentaEffects, DashEffect, dashEnd, now, DamageType.Physical, 1.3, 10, "Vilenta", forceSpell: false) is { } dash)
                    {
                        attacks.Add(new(now + 0.65, bossId, boss, dash.Attack, pos, dashEnd, angle, 9, AttackShape.DashLine, GroundSlamArt));
                        return;
                    }
                }
                else if (step % 5 == 1)
                {
                    // Directional geometry slashes (9083 Left / 9085 Right).
                    int slashEffect = (step & 1) == 0 ? GeometryLeftEffect : GeometryRightEffect;
                    if (await CastAction(runner, bossId, boss, VilentaEffects, slashEffect, aim, now, DamageType.Fire, 1.35, 22, "Vilenta", forceSpell: false) is { } slash)
                    {
                        double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                        attacks.Add(new(now + 0.85, bossId, boss, slash.Attack, pos, aim, facing, 22, AttackShape.Cone, ComboExplodeArt));
                        return;
                    }
                }
                else if (step % 5 == 2)
                {
                    // VilentaXMortar[6388]: X-pattern 5-spot fire mortar barrage.
                    if (await CastAction(runner, bossId, boss, VilentaEffects, XMortarEffect, aim, now, DamageType.Fire, 1.25, 11, "Vilenta", forceSpell: true) is { } xm)
                    {
                        (int DX, int DY)[] offsets = [(0, 0), (11, 11), (-11, 11), (11, -11), (-11, -11)];
                        foreach (var (dx, dy) in offsets)
                        {
                            var drop = WalkableNear(runner, (aim.X + dx, aim.Y + dy));
                            await SpawnTimedEffect(runner, effects, drop, MortarArt, now + 1.25, scale: 1.05f);
                            attacks.Add(new(now + 1.1, bossId, boss, xm.Attack, pos, drop, 0, 10, AttackShape.Circle, ComboExplodeArt));
                        }
                        return;
                    }
                }
                else if (step % 5 == 3)
                {
                    // VilentaSunder[5637]: linear shockwave slam toward target.
                    if (await CastAction(runner, bossId, boss, VilentaEffects, SunderEffect, aim, now, DamageType.Physical, 1.4, 12, "Vilenta", forceSpell: false) is { } sunder)
                    {
                        double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                        for (int i = 1; i <= 4; i++)
                        {
                            var spot = WalkableNear(runner, (
                                (int)Math.Round(pos.X + Math.Cos(facing) * (i * 10)),
                                (int)Math.Round(pos.Y + Math.Sin(facing) * (i * 10))));
                            attacks.Add(new(now + 0.55 + i * 0.25, bossId, boss, sunder.Attack, pos, spot, facing, 10, AttackShape.Circle, GroundSlamArt));
                        }
                        return;
                    }
                }
                else
                {
                    // VilentaGasMortar[8904]: fire + lingering gas pool.
                    if (await CastAction(runner, bossId, boss, VilentaEffects, GasMortarEffect, aim, now, DamageType.Fire, 1.2, 12, "Vilenta", forceSpell: true) is { } gas)
                    {
                        await SpawnTimedEffect(runner, effects, aim, BombingAoeArt, now + 1.2, scale: 1.15f);
                        attacks.Add(new(now + 1.05, bossId, boss, gas.Attack, pos, aim, 0, 12, AttackShape.Circle, GasMortarExplodeArt, LeavesGas: true));
                        return;
                    }
                }
            }

            if (dist <= 16)
            {
                int meleeEff = enraged ? EnrageMeleeEffect : MeleeEffect;
                double scale = enraged ? 1.25 : 1.05;
                if (await CastAction(runner, bossId, boss, VilentaEffects, meleeEff, aim, now, DamageType.Physical, scale, 14, "Vilenta", forceSpell: false) is { } melee)
                    attacks.Add(new(now + melee.Attack.Seconds * 0.45, bossId, boss, melee.Attack, pos, aim, 0, 13, AttackShape.Circle, enraged ? RageBeamArt : null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "Vilenta");
            }
        }

        async Task BeginAirborneIntermission(Player runner, uint ownerId, SpawnedMonster boss, int phaseIdx, double now)
        {
            airborne = true;
            airborneEndsAt = now + 14.0;
            carpetLaneIndex = 0;
            nextCarpetLaneAt = now + 1.5;
            attacks.Clear();

            var pos = boss.Walk.Position(now);
            await CastAction(runner, ownerId, boss, VilentaEffects, HideEffect, pos, now, DamageType.Fire, 0.5, 10, "Vilenta");
            SetInvulnerable(runner, ownerId, boss, true, CannotBeDamagedBuff);
            await SetWorldHidden(runner, ownerId, boss, true);

            // Spawn Test Subject & Kiln Crawler add wave (NoRewards = true).
            var rng = AreaRng(runner);
            for (int i = 0; i < 4; i++)
            {
                var spot = zombieSpawners.Count > 0
                    ? zombieSpawners[i % zombieSpawners.Count]
                    : RingSpot(runner, arenaCenter, i * (Math.Tau / 4.0), 20);
                string add = i switch
                {
                    0 => TestSubjectMale,
                    1 => TestSubjectFemale,
                    2 => KilnCrawlerAxe,
                    _ => KilnCrawlerSword,
                };
                await SpawnAdd(runner, add, boss.Level, spot, ownerId, waveAdds, ownedCurrent: () => !stopped && !cleaned);
            }
            Console.WriteLine($"[gateway] Vilenta airborne carpet-bomb intermission {phaseIdx + 1}/2 started");
        }

        async Task StepAirborneIntermission(Player runner, uint ownerId, SpawnedMonster boss, Player target, double now)
        {
            // Sweep 3 fire/gas carpet-bomb lanes across the arena using VilentaGasMortarMulti[8906].
            if (carpetLaneIndex < 3 && now >= nextCarpetLaneAt)
            {
                nextCarpetLaneAt = now + 2.8;
                int lane = carpetLaneIndex++;
                var aim = target.Walk.Position(now);
                var origin = carpetBombAnchors.Count > 0
                    ? carpetBombAnchors[lane % carpetBombAnchors.Count]
                    : RingSpot(runner, arenaCenter, lane * (Math.Tau / 3.0), 26);
                double dir = Math.Atan2(aim.Y - origin.Y, aim.X - origin.X);
                var bombAtk = BuildAttack(runner, boss, VilentaEffects, GasMortarMultiEffect, DamageType.Fire, 1.3, 12, forceSpell: true);
                for (int i = 0; i < 5; i++)
                {
                    var drop = WalkableNear(runner, (
                        (int)Math.Round(origin.X + Math.Cos(dir) * (i * 12)),
                        (int)Math.Round(origin.Y + Math.Sin(dir) * (i * 12))));
                    double impactAt = now + 1.0 + i * 0.35;
                    await SpawnTimedEffect(runner, effects, drop, BombingAoeArt, impactAt + 0.3, scale: 1.2f);
                    attacks.Add(new(impactAt, ownerId, boss, bombAtk, origin, drop, dir, 11, AttackShape.Circle, GasMortarExplodeArt, LeavesGas: true));
                }
            }

            bool addsAlive = waveAdds.Any(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            if ((carpetLaneIndex < 3 || addsAlive) && now < airborneEndsAt)
                return;

            airborne = false;
            waveAdds.Clear();
            var landAt = RingSpot(runner, target.Walk.Position(now), AreaRng(runner).NextDouble() * Math.Tau, 12);
            await TeleportBoss(runner, ownerId, boss, landAt, now);
            await SetWorldHidden(runner, ownerId, boss, false);
            SetInvulnerable(runner, ownerId, boss, false, CannotBeDamagedBuff);
            await SpawnTimedEffect(runner, effects, landAt, GroundSlamArt, now + 1.6, scale: 1.3f);
            MarkPhaseCompleted(runner.Instance, PhaseKey, boss.LifePhases!.Completed);
            boss.LifePhases.Finish();
            boss.BusyUntil = now + 0.9;
            Console.WriteLine($"[gateway] Vilenta landed after carpet-bomb intermission {boss.LifePhases.Completed}/2");
        }

        async Task StepDelayedAttacks(Player runner, double now)
        {
            for (int i = attacks.Count - 1; i >= 0; i--)
            {
                var a = attacks[i];
                if (now < a.At) continue;
                attacks.RemoveAt(i);
                if (!Alive(a.Source)) continue;
                switch (a.Shape)
                {
                    case AttackShape.DashLine:
                        await TeleportBoss(runner, a.SourceId, a.Source, a.To, now);
                        await HitSegment(runner, a.SourceId, a.Source, a.Attack, a.From, a.To, a.Radius, () => !stopped && Alive(a.Source));
                        break;
                    case AttackShape.Cone:
                        await HitCone(runner, a.SourceId, a.Source, a.Attack, a.From, a.Facing, 0.72, a.Radius, () => !stopped && Alive(a.Source));
                        break;
                    default:
                        await HitCircle(runner, a.SourceId, a.Source, a.Attack, a.To, a.Radius, () => !stopped && Alive(a.Source));
                        break;
                }
                if (a.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, a.To, ao, now + 1.3, scale: 1.2f);
                if (a.LeavesGas && gasPools.Count < 14)
                {
                    gasPools.Add(new(a.To, 10, now + 8.0));
                    await SpawnTimedEffect(runner, effects, a.To, GasCloudArt, now + 8.0, scale: 1.2f);
                }
            }
        }

        async Task StepGasPools(Player runner, uint ownerId, SpawnedMonster boss, double now)
        {
            if (now < nextGasTick) return;
            nextGasTick = now + 0.25;
            var (minDmg, maxDmg) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (32, 48);
            double gasDps = (minDmg + maxDmg) * 0.5;

            foreach (var p in ActivePlayers(runner))
            {
                var at = p.Walk.Position(now);
                bool inGas = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && gasPools.Any(g => Distance(at, g.At) <= g.Radius);
                if (inGas)
                    await HoldPlayerDebuff(p, gasHolder, CausticCloudBuff, ownerId, EmptyStats);
                else
                    await ReleasePlayerDebuff(p, gasHolder);

                if (!gasExposures.TryGetValue(p, out var exp))
                    gasExposures[p] = exp = new();
                await ArenaHazards.Ground(p, exp, now, (pt, _) =>
                    gasPools.Any(g => Distance(pt, g.At) <= g.Radius)
                        ? DamagePacket.Of(DamageType.Fire, gasDps * 0.5, gasDps * 0.5).Add(DamageType.Chaos, new(gasDps * 0.5, gasDps * 0.5))
                        : DamagePacket.Empty,
                    () => !stopped && !cleaned);
            }
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            airborne = false;
            gatesLocked = false;
            attacks.Clear();
            gasPools.Clear();
            foreach (var g in Gates) await SetGateLocked(runner, g, false);
            if (bossId != 0 && runner.Area.Monsters.TryGetValue(bossId, out var b))
            {
                SetInvulnerable(runner, bossId, b, false, CannotBeDamagedBuff);
                b.RemoveTemporaryStats("vilenta_enrage");
                runner.Area.Buffs.Clear(bossId, EnrageBuff, bossId);
                if (b.WorldHidden) await SetWorldHidden(runner, bossId, b, false);
            }
            await RetireMonsters(runner, waveAdds);
            waveAdds.Clear();
            foreach (var p in ActivePlayers(runner))
                await ReleasePlayerDebuffs(p);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Vilenta defeated; arena doors unlocked, gas pools and adds cleared");
        }
    }
}

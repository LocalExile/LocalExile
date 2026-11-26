using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 7 — The Dread Thicket (<c>2_7_9</c>) Gruthkul, Mother of Despair (<c>DespairBear[6047]</c>) encounter:
/// <list type="bullet">
/// <item>All 4 <c>TriggerSpawnersDespairBearProjectiles1..4[6774..6777]</c> <b>Despair Shadow-Orb Wave</b> curtains
/// sweeping across the arena with exploding despair orbs and lingering dark ground fissures.</item>
/// <item>Telegraphed <c>DespairBearUpheaval[6763]</c> multi-crack shockwaves and <c>DespairBearEarthquake[6765]</c>
/// slam + delayed aftershock.</item>
/// <item>Sub-50% HP enrage (<c>despair_bear_buff</c> 868) accelerating attack speed, movement speed, and wave cadence.</item>
/// </list>
/// </summary>
public static class GruthkulEncounter
{
    public const string Area = "2_7_9";
    public const string Boss = "Metadata/Monsters/DespairBear/DespairBear"; // MV 6047

    const string PhaseKey = "Gruthkul";
    static readonly int[] PhaseThresholds = [50];
    static readonly int[] BossEffects = [420, 6763, 6765, 6774, 6775, 6776, 6777];

    const ushort EnrageBuffRow = 868;  // despair_bear_buff
    const ushort SlowDebuffRow = 880;  // despair_bear_slow
    const string EnrageStatsKey = "gruthkul_enrage";

    static readonly IReadOnlyDictionary<uint, int> EnrageStats = new Dictionary<uint, int>
    {
        [Stat.AttackSpeedPlusPct] = 35,
        [Stat.BaseMovementVelocityPlusPct] = 25,
        [Stat.PhysicalDamagePlusPct] = 20,
    };

    static readonly IReadOnlyDictionary<uint, int> DespairSlowStats = new Dictionary<uint, int>
    {
        [Stat.MovementVelocityPlusPct] = -30,
    };

    const string OrbProjArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/ground_projectile/projectile.ao";
    const string OrbSurgeArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/ground_projectile/surge.ao";
    const string CrackArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/ground_crack/groundcrack1.ao";
    const string UpheavalArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/remote_slam/upheaval/crack1.ao";
    const string GroundSlamArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/ground_slam/groundslam.ao";
    const string RoomPulseArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/room_pulse/roompulse.ao";
    const string RoarArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/roar/roar.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> FissureExposures = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id == Boss))
                return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player));
        }
        await fight.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return Task.CompletedTask;
        if (FissureExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        if (!player.Closed) player.Area.Buffs.Clear(player.Id, SlowDebuffRow, player.Id);
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in FissureExposures) pair.Value.Last = double.NaN;
        }
    }

    sealed record DarkFissure((int X, int Y) At, double Ends, double Radius);
    sealed class ShadowOrb(uint effectId, (int X, int Y) from, (int X, int Y) to, double started, double ends)
    {
        public readonly uint EffectId = effectId;
        public readonly (int X, int Y) From = from, To = to;
        public readonly double Started = started, Ends = ends;
        public readonly HashSet<Player> Hit = [];
        public (int X, int Y) Position(double now)
        {
            double t = Math.Clamp((now - Started) / Math.Max(0.1, Ends - Started), 0, 1);
            return ((int)Math.Round(From.X + (To.X - From.X) * t), (int)Math.Round(From.Y + (To.Y - From.Y) * t));
        }
    }

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<(int X, int Y)> crackSpots;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<DarkFissure> fissures = [];
        readonly List<ShadowOrb> orbs = [];

        uint bossId;
        bool initialized, enraged, cleaned, stopped;
        double nextChaseAt, nextWaveAt, nextUpheavalAt, nextQuakeAt;
        int waveCursor;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            crackSpots = [.. placed.Of("Metadata/Monsters/DespairBear/BearCrack")
                .Concat(placed.Of("Metadata/Monsters/DespairBear/BearCrackInert"))
                .Select(p => (p.X, p.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bossId != 0 && area.Monsters.TryGetValue(bossId, out var b))
            {
                b.RemoveTemporaryStats(EnrageStatsKey);
                b.RebuildStats();
                area.Buffs.Clear(bossId, EnrageBuffRow, bossId);
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            fissures.RemoveAll(f => now >= f.Ends);

            // Read the boss by its remembered id first; scan the area only when that lookup is gone.
            SpawnedMonster? boss = null;
            if (bossId != 0 && runner.Area.Monsters.TryGetValue(bossId, out var cached) && cached.Hostile && cached.Variety?.Id == Boss)
                boss = cached;
            else
            {
                var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Boss);
                if (entry.Value is { } found)
                {
                    bossId = entry.Key;
                    boss = found;
                }
            }
            if (boss is null)
                return;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner, boss);
                return;
            }

            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: bossId);
            if (target is null)
            {
                ResetExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                boss.Aggro = true;
                MarkStarted(runner.Instance, PhaseKey);
                await BindEffects(runner, bossId, boss, BossEffects);
                await EnsureBossBar(runner, bossId, boss);
                if (boss.LifePhases!.Completed > 0) ApplyEnrage(runner, boss);
                nextWaveAt = now + 4.5;
                nextUpheavalAt = now + 3.5;
                nextQuakeAt = now + 7.5;
                Console.WriteLine($"[gateway] Gruthkul controller active for 0x{bossId:x} (enraged={enraged})");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, bossId, boss);
            }

            // Sub-50% HP enrage
            var phases = boss.LifePhases!;
            if (!enraged && phases.Completed < 1 && phases.Begin(boss.Life, boss.MaxLife))
            {
                ApplyEnrage(runner, boss);
                MarkPhaseCompleted(runner.Instance, PhaseKey, 0);
                phases.Finish();
                await SpawnTimedEffect(runner, effects, pos, RoarArt, now + 1.8, scale: 1.35f);
                Console.WriteLine($"[gateway] Gruthkul 0x{bossId:x}: sub-50% HP enraged!");
            }

            await StepShadowOrbs(runner, boss, now);
            await StepFissures(runner, boss, now);

            if (!CanAct(boss, now)) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // Despair Shadow-Orb Wave Curtain — rotates all 4 TriggerSpawnersDespairBearProjectiles1..4 [6774..6777]
            if (now >= nextWaveAt)
            {
                nextWaveAt = now + (enraged ? 6.5 : 9.5);
                int pattern = waveCursor++ % 4;
                int effect = 6774 + pattern;
                if (await CastAction(runner, bossId, boss, effect, aim, 1.35, now, $"[Gruthkul] Despair Wave {pattern + 1}") is not null)
                {
                    await SpawnTimedEffect(runner, effects, pos, RoomPulseArt, now + 1.4, scale: 1.3f);
                    await LaunchShadowOrbCurtain(runner, boss, pos, aim, pattern, now);
                    return;
                }
            }

            // DespairBearEarthquake[6765] — slam + delayed aftershock + dark fissure
            if (now >= nextQuakeAt && dist <= 34)
            {
                nextQuakeAt = now + (enraged ? 6.0 : 8.5);
                if (await CastAction(runner, bossId, boss, 6765, aim, 1.4, now, "[Gruthkul] Earthquake") is { } gen)
                {
                    var slamAt = WalkableNear(runner, aim);
                    await SpawnTimedEffect(runner, effects, slamAt, GroundSlamArt, now + 1.6, scale: 1.3f);
                    await SpawnTimedEffect(runner, effects, slamAt, CrackArt, now + 8.5, scale: 1.15f);
                    fissures.Add(new(slamAt, now + 8.5, 11.0));
                    hits.Add(new(now + 0.6, bossId, boss, slamAt, 14, PhysicalAttack(boss, 1.1, 14), gen));
                    hits.Add(new(now + 1.85, bossId, boss, slamAt, 20,
                        HybridAttack(boss, DamageType.Chaos, 1.0, 0.7, 20), null, RoomPulseArt, 1.35f));
                    return;
                }
            }

            // DespairBearUpheaval[6763] — line of dark fissures + spikes
            if (now >= nextUpheavalAt && dist <= 56)
            {
                nextUpheavalAt = now + (enraged ? 4.8 : 6.8);
                if (await CastAction(runner, bossId, boss, 6763, aim, 1.2, now, "[Gruthkul] Upheaval") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = HybridAttack(boss, DamageType.Chaos, 0.85, 0.65, 11);
                    for (int s = 1; s <= 4; s++)
                    {
                        var spot = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(angle) * (s * 12)),
                            (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 12))));
                        await SpawnTimedEffect(runner, effects, spot, UpheavalArt, now + 0.6 + s * 0.2, scale: 1.05f);
                        hits.Add(new(now + 0.55 + s * 0.2, bossId, boss, spot, 10, atk, gen, CrackArt, 1.0f));
                        fissures.Add(new(spot, now + 6.5, 8.0));
                    }
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: enraged ? 1.2 : 1.05, meleeEffect: 420);
        }

        void ApplyEnrage(Player runner, SpawnedMonster boss)
        {
            enraged = true;
            boss.SetTemporaryStats(EnrageStatsKey, EnrageStats);
            boss.RebuildStats();
            runner.Area.Buffs.Set(bossId, new(EnrageBuffRow, bossId));
        }

        async Task LaunchShadowOrbCurtain(Player runner, SpawnedMonster boss, (int X, int Y) center, (int X, int Y) aim, int pattern, double now)
        {
            // 4 cardinal/diagonal curtain directions matching TriggerSpawnersDespairBearProjectiles1..4
            double dirAngle = pattern * (Math.PI / 2.0) + (enraged ? Math.PI / 4.0 : 0.0);
            double perpAngle = dirAngle + Math.PI / 2.0;
            int count = enraged ? 6 : 5;
            double travelDist = 68;
            double speed = enraged ? 22.0 : 18.0;
            double duration = travelDist / speed;

            for (int i = 0; i < count; i++)
            {
                double lateral = (i - (count - 1) / 2.0) * 14.0;
                (int X, int Y) from = (
                    (int)Math.Round(aim.X - Math.Cos(dirAngle) * 34 + Math.Cos(perpAngle) * lateral),
                    (int)Math.Round(aim.Y - Math.Sin(dirAngle) * 34 + Math.Sin(perpAngle) * lateral));
                (int X, int Y) to = (
                    (int)Math.Round(from.X + Math.Cos(dirAngle) * travelDist),
                    (int)Math.Round(from.Y + Math.Sin(dirAngle) * travelDist));
                uint fx = await SpawnTimedEffect(runner, effects, from, OrbProjArt, now + duration, scale: 1.05f);
                await SendWorldRecord(runner, fx, InstanceProtocol.MoveTo(fx, to.X, to.Y, (float)duration, InstanceProtocol.FollowTerrain), null);
                orbs.Add(new(fx, from, to, now, now + duration));
            }

            // Also ignite a cached BearCrack spot if present
            if (crackSpots.Count > 0)
            {
                var crack = crackSpots[AreaRng(runner).Next(crackSpots.Count)];
                await SpawnTimedEffect(runner, effects, crack, CrackArt, now + 7.5, scale: 1.15f);
                fissures.Add(new(crack, now + 7.5, 10.0));
            }
        }

        async Task StepShadowOrbs(Player runner, SpawnedMonster boss, double now)
        {
            if (orbs.Count == 0) return;
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            var orbAtk = HybridAttack(boss, DamageType.Chaos, 0.45, 0.85, 8, spell: true);
            for (int i = orbs.Count - 1; i >= 0; i--)
            {
                var orb = orbs[i];
                var cur = orb.Position(now);
                bool hitAny = false;
                foreach (var p in players)
                {
                    if (orb.Hit.Contains(p)) continue;
                    if (Distance(p.Walk.Position(now), cur) <= 7.5)
                    {
                        orb.Hit.Add(p);
                        hitAny = true;
                        await AddPlayerDebuff(p, SlowDebuffRow, bossId, DespairSlowStats, 2.5);
                        await ArenaHazards.Hit(p, bossId, boss, orbAtk, at => Distance(at, cur) <= 9, () => !stopped && Alive(boss));
                    }
                }
                if (now >= orb.Ends || hitAny)
                {
                    orbs.RemoveAt(i);
                    await SpawnTimedEffect(runner, effects, cur, OrbSurgeArt, now + 0.9, scale: 1.0f);
                }
            }
        }

        async Task StepFissures(Player runner, SpawnedMonster boss, double now)
        {
            if (fissures.Count == 0)
            {
                ResetExposures(runner);
                return;
            }
            var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (25, 35);
            double dps = (min + max) * 0.42;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = FissureExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                    fissures.Any(f => Distance(at, f.At) <= f.Radius)
                        ? DamagePacket.Of(DamageType.Chaos, dps * 0.6, dps * 0.6).Add(DamageType.Physical, new(dps * 0.4, dps * 0.4))
                        : DamagePacket.Empty,
                    () => !stopped && Alive(boss));
            }
        }

        static void ResetExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (FissureExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task Cleanup(Player runner, SpawnedMonster boss)
        {
            cleaned = true;
            boss.RemoveTemporaryStats(EnrageStatsKey);
            boss.RebuildStats();
            runner.Area.Buffs.Clear(bossId, EnrageBuffRow, bossId);
            hits.Clear();
            orbs.Clear();
            fissures.Clear();
            ResetExposures(runner);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Gruthkul defeated; shadow orbs and fissures cleared");
        }
    }
}

using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 6 — The Wetlands (<c>2_6_11</c>) Ryslatha, the Puppet Mistress full retail encounter controller:
/// <list type="bullet">
/// <item><b>Correct spawn order</b>: <see cref="Place"/> spawns Ryslatha (<c>InsectSpawnerParasiteBoss[1610]</c>)
/// active from pull alongside her 3 dormant invulnerable nests (<c>ParasiteNestRyslatha[4939]</c>).</item>
/// <item><b>Cocoon intermissions at [75, 50, 25]% HP</b>: Ryslatha retreats into her ceiling cocoon
/// (<c>WorldHidden</c> + <c>SetInvulnerable</c>) while the next nest unlocks and spawns infested adds
/// (<c>HellionParasiteEmerge[358]</c>, <c>DropBearGround2ParasiteEmerge[3308]</c>,
/// <c>FrogParasiteSpawnParasiteEmerge[1551]</c>, <c>NoRewards = true</c>); destroying that nest brings Ryslatha back
/// down. Survives portal re-entry via <c>SyncSavedPhases</c>.</item>
/// <item><b>Signature skills</b>: Volatile brood eggs (<c>RyslathaEgg[6083]</c> / <c>RyslathaSummonEgg[3432]</c>),
/// <c>RyslathaSlide[8449]</c> powerslide leaving caustic chaos trail, <c>InsectSpawnerSpitParasiteBoss[2069]</c>, and
/// <c>FlamebreathParasiteBoss[6266]</c>.</item>
/// </list>
/// </summary>
public static class RyslathaEncounter
{
    public const string Area = "2_6_11";
    public const string Nest = "Metadata/Monsters/ParasiteNest/ParasiteNestRyslatha";         // MV 4939
    public const string Boss = "Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss";   // MV 1610
    public const string Egg = "Metadata/Monsters/SpiderlingEgg/RyslathaEgg";                  // MV 6083

    public const string InfestedHellion = "Metadata/Monsters/Hellion/HellionParasiteEmerge";               // MV 358
    public const string InfestedDropBear = "Metadata/Monsters/DropBear/DropBearGround2ParasiteEmerge";     // MV 3308
    public const string InfestedFrog = "Metadata/Monsters/Frog/FrogParasiteSpawnParasiteEmerge";           // MV 1551

    const string PhaseKey = "Ryslatha";
    static readonly int[] PhaseThresholds = [75, 50, 25];
    static readonly int[] BossEffects = [2069, 6266, 3432, 8449, 3434];

    const string SlideArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/powerslide/rig.ao";
    const string SlideGroundArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/powerslide/ground_rig.ao";
    const string EggExplodeArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/spit_parasite/parasiteeggexplodenew.ao";
    const string SpitArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/spit_parasite/rig.ao";
    const string FlameImpactArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/poison_flamethrower/impact/rig.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> ChaosExposures = new();

    /// <summary>Preserved exact signature called by <c>Monsters.Population.cs</c> and verified by
    /// <c>RyslathaEncounterTests</c>: emits only the 3 <c>ParasiteNestRyslatha</c> nests and returns the boss arena
    /// coordinate (recorded into <c>LaterFormPoints[Boss]</c>); <see cref="Fight.Step"/> spawns Ryslatha active from
    /// pull while holding the 3 nests dormant and invulnerable.</summary>
    internal static (int X, int Y)? Place(IReadOnlyList<AreaPopulation.RoomPoint> points, Func<int, int, bool> walkable,
        List<AreaPopulation.Spawn> spawns)
    {
        if (Monsters.Find(Nest) is not { Supported: true, Id: Nest } nest
            || Monsters.Find(Boss) is not { Supported: true, Id: Boss }
            || points.FirstOrDefault(p => p.Name == "mapboss") is not { } bossPt
            || AreaPopulation.BossCell(bossPt, walkable) is not { } arena)
            return null;
        var positions = points.Where(p => p.Name == Nest).Select(p => (p.X, p.Y)).Distinct().ToList();
        if (positions.Count != 3 || positions.Any(p => !walkable(p.X, p.Y))) return null;
        foreach (var p in positions)
            spawns.Add(new(nest.Row, p.X, p.Y, MonsterRarity.Unique, "Ryslatha's nest"));
        return arena;
    }

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id is Boss or Nest))
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
        if (ChaosExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in ChaosExposures) pair.Value.Last = double.NaN;
        }
    }

    sealed record ChaosPool((int X, int Y) At, double Ends, double Radius);
    sealed record VolatileEgg(uint Id, (int X, int Y) At, double DetonateAt);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly (int X, int Y)? cachedMiddle;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<ChaosPool> pools = [];
        readonly List<VolatileEgg> eggs = [];
        readonly HashSet<uint> adds = [];

        uint bossId, activeNestId;
        (int X, int Y) arenaCenter;
        bool initialized, cocooned, cleaned, stopped;
        double nextChaseAt, nextSlideAt, nextEggAt, nextBreathAt, nextNestSpawnAt;
        int nestSpawnCount;

        public Fight(Player owner)
        {
            area = owner.Area;
            cachedMiddle = AreaPlacements(owner).Of("Metadata/MiscellaneousObjects/ArenaMiddle").FirstOrDefault() is { } m
                ? (m.X, m.Y) : null;
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bossId != 0 && area.Monsters.TryGetValue(bossId, out var b))
            {
                ClearInvulnerableOnUnload(area, bossId, b, CannotBeDamagedBuff);
                b.WorldHidden = false;
            }
            foreach (var (nid, nm) in area.Monsters.Where(p => p.Value.Variety?.Id == Nest))
                ClearInvulnerableOnUnload(area, nid, nm, CannotBeDamagedBuff);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            pools.RemoveAll(p => now >= p.Ends);

            var livingNests = runner.Area.Monsters
                .Where(p => p.Value.Variety?.Id == Nest && Alive(p.Value))
                .OrderBy(p => p.Key)
                .ToList();

            var bossEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Boss);
            if (bossEntry.Value is null)
            {
                // Spawn Ryslatha active from pull (before nests are killed) using the recorded boss point,
                // unless she has already been defeated in this instance.
                if (runner.Instance.HasState(PhaseFlag(PhaseKey, 3)) || livingNests.Count == 0)
                    return;
                // Hold nests dormant immediately while spawning Ryslatha so players cannot break them first.
                foreach (var (nid, nm) in livingNests)
                {
                    nm.NoRewards = true;
                    if (nm.Effects[Stat.BaseCannotBeDamaged] == 0)
                        SetInvulnerable(runner, nid, nm, true, CannotBeDamagedBuff);
                }
                if (Monsters.Find(Boss) is not { Supported: true, Id: Boss } bossVar)
                    return;
                var nestCentroid = (
                    (int)Math.Round(livingNests.Average(n => n.Value.Walk.Position(now).X)),
                    (int)Math.Round(livingNests.Average(n => n.Value.Walk.Position(now).Y)));
                var spawnAt = WalkableNear(runner, runner.LaterFormPoints.GetValueOrDefault(Boss, cachedMiddle ?? nestCentroid));
                byte level = livingNests[0].Value.Level;
                uint spawnedId = await SpawnMonster(runner, bossVar, level, MonsterRarity.Unique, spawnAt.X, spawnAt.Y,
                    instanceOwned: true, ownedCurrent: () => !stopped && !cleaned);
                if (spawnedId == 0 || !runner.Area.Monsters.TryGetValue(spawnedId, out var spawnedBoss))
                    return;
                bossEntry = new(spawnedId, spawnedBoss);
            }
            var boss = bossEntry.Value;
            bossId = bossEntry.Key;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner, boss);
                return;
            }

            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);

            // Keep all non-active nests dormant & invulnerable (and NoRewards so they don't drop loot prematurely).
            foreach (var (nid, nm) in livingNests)
            {
                nm.NoRewards = true;
                bool shouldBeInvuln = !cocooned || nid != activeNestId;
                bool isInvuln = nm.Effects[Stat.BaseCannotBeDamaged] != 0;
                if (shouldBeInvuln != isInvuln)
                    SetInvulnerable(runner, nid, nm, shouldBeInvuln, CannotBeDamagedBuff);
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: cocooned ? null : bossId);
            if (target is null)
            {
                ResetChaosExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                arenaCenter = cachedMiddle ?? pos;
                boss.Aggro = true;
                MarkStarted(runner.Instance, PhaseKey);
                await BindEffects(runner, bossId, boss, BossEffects);
                await EnsureBossBar(runner, bossId, boss);
                nextSlideAt = now + 5.5;
                nextEggAt = now + 4.5;
                nextBreathAt = now + 7.5;
                Console.WriteLine($"[gateway] Ryslatha controller active for 0x{bossId:x} ({livingNests.Count} nests, completed {boss.LifePhases!.Completed})");
            }
            else
            {
                boss.Target = target;
                if (!cocooned) await EnsureBossBar(runner, bossId, boss);
            }

            await StepEggs(runner, boss, now);
            await StepChaosGround(runner, boss, now);

            var phases = boss.LifePhases!;

            // Handle active ceiling-cocoon phase: wait for active nest to be destroyed while spawning infested adds
            if (cocooned)
            {
                SpawnedMonster? activeNest = null;
                bool activeNestAlive = activeNestId != 0
                    && runner.Area.Monsters.TryGetValue(activeNestId, out activeNest)
                    && Alive(activeNest);
                if (activeNestAlive)
                {
                    if (now >= nextNestSpawnAt && nestSpawnCount < 6)
                    {
                        nextNestSpawnAt = now + 3.2;
                        await SpawnNestInfested(runner, boss, activeNest!.Walk.Position(now));
                    }
                    return;
                }

                // Active nest destroyed -> bring Ryslatha back down!
                cocooned = false;
                activeNestId = 0;
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
                await SetWorldHidden(runner, bossId, boss, false);
                boss.Walk.Teleport(arenaCenter.X, arenaCenter.Y, now);
                await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, arenaCenter.X, arenaCenter.Y), null);
                MarkPhaseCompleted(runner.Instance, PhaseKey, phases.Completed);
                phases.Finish();
                nextSlideAt = now + 3.5;
                nextEggAt = now + 4.0;
                Console.WriteLine($"[gateway] Ryslatha 0x{bossId:x}: nest destroyed ({phases.Completed}/3); Ryslatha descended");
                return;
            }

            // Guarantee vulnerability when not cocooned
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            if (boss.WorldHidden)
                await SetWorldHidden(runner, bossId, boss, false);

            // Trigger ceiling cocoon at [75, 50, 25]% HP while at least one nest remains
            if (!phases.Active && phases.Completed < 3 && livingNests.Count > 0 && phases.Begin(boss.Life, boss.MaxLife))
            {
                cocooned = true;
                var nestPick = livingNests[0];
                activeNestId = nestPick.Key;
                nestSpawnCount = 0;
                nextNestSpawnAt = now + 0.8;
                SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                await SetWorldHidden(runner, bossId, boss, true);
                SetInvulnerable(runner, activeNestId, nestPick.Value, false, CannotBeDamagedBuff);
                await SpawnTimedEffect(runner, effects, nestPick.Value.Walk.Position(now), SpitArt, now + 2.0, scale: 1.3f);
                Console.WriteLine($"[gateway] Ryslatha 0x{bossId:x}: cocoon intermission {phases.Completed + 1}/3 -> nest 0x{activeNestId:x} vulnerable");
                return;
            }

            if (!CanAct(boss, now)) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // RyslathaSlide[8449] — powerslide leaving caustic chaos ground trail
            if (now >= nextSlideAt && dist is >= 10 and <= 55)
            {
                nextSlideAt = now + 9.0;
                var dest = WalkableNear(runner, aim);
                if (await CastAction(runner, bossId, boss, 8449, dest, 1.35, now, "[Ryslatha] Powerslide") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, pos, SlideArt, now + 1.4, scale: 1.15f);
                    for (int s = 0; s <= 3; s++)
                    {
                        double t = s / 3.0;
                        var pt = WalkableNear(runner, (
                            (int)Math.Round(pos.X + (dest.X - pos.X) * t),
                            (int)Math.Round(pos.Y + (dest.Y - pos.Y) * t)));
                        await SpawnTimedEffect(runner, effects, pt, SlideGroundArt, now + 7.5, scale: 1.0f);
                        pools.Add(new(pt, now + 7.5, 8.5));
                    }
                    boss.Walk.Teleport(dest.X, dest.Y, now);
                    await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, dest.X, dest.Y), null);
                    hits.Add(new(now + 0.6, bossId, boss, dest, 12,
                        HybridAttack(boss, DamageType.Chaos, 0.7, 0.7, 12), gen));
                    return;
                }
            }

            // RyslathaSummonEgg[3432] — volatile brood eggs (RyslathaSpawnEggsDaemon parity)
            if (now >= nextEggAt)
            {
                nextEggAt = now + 8.5;
                if (eggs.Count < 6 && await CastAction(runner, bossId, boss, 3432, aim, 1.0, now, "[Ryslatha] Spawn Eggs") is not null)
                {
                    var rng = AreaRng(runner);
                    for (int i = 0; i < 3 && eggs.Count < 6; i++)
                    {
                        var eSpot = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 6 + i * 5);
                        uint eid = await SpawnAdd(runner, Egg, boss.Level, eSpot, bossId,
                            ownedCurrent: () => !stopped && !cleaned && Alive(boss));
                        if (eid != 0)
                            eggs.Add(new(eid, eSpot, now + 3.2 + i * 0.35));
                    }
                    return;
                }
            }

            // FlamebreathParasiteBoss[6266] — caustic/fire breath cone
            if (now >= nextBreathAt && dist <= 36)
            {
                nextBreathAt = now + 6.5;
                if (await CastAction(runner, bossId, boss, 6266, aim, 1.35, now, "[Ryslatha] Caustic Breath") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = HybridAttack(boss, DamageType.Chaos, 0.0, 1.15, 32, spell: true);
                    await SpawnTimedEffect(runner, effects, aim, FlameImpactArt, now + 1.2, scale: 1.2f);
                    await HitBeamLine(runner, bossId, boss, pos, angle, 34, 8.0, atk, () => !stopped && Alive(boss));
                    pools.Add(new(WalkableNear(runner, aim), now + 6.0, 9.0));
                    await SpawnTimedEffect(runner, effects, aim, SlideGroundArt, now + 6.0, scale: 1.0f);
                    return;
                }
            }

            // InsectSpawnerSpitParasiteBoss[2069] ranged spit / chase
            if (dist <= 45)
            {
                if (await CastAction(runner, bossId, boss, 2069, aim, 1.1, now, "[Ryslatha] Parasite Spit") is { } gen)
                {
                    hits.Add(new(now + 0.65, bossId, boss, aim, 9.5,
                        ElementalSpellAttack(boss, DamageType.Chaos, 0.9, 9.5), gen, SpitArt, 1.0f));
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: 0.95, meleeEffect: 2069);
        }

        async Task SpawnNestInfested(Player runner, SpawnedMonster boss, (int X, int Y) nestPos)
        {
            int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            if (living >= 6) return;
            var rng = AreaRng(runner);
            string[] types = [InfestedHellion, InfestedDropBear, InfestedFrog];
            for (int i = 0; i < 2 && living + i < 6 && nestSpawnCount < 6; i++)
            {
                string kind = types[nestSpawnCount++ % types.Length];
                var spot = RingSpot(runner, nestPos, rng.NextDouble() * Math.Tau, 8);
                await SpawnAdd(runner, kind, boss.Level, spot, bossId, adds,
                    ownedCurrent: () => !stopped && !cleaned && Alive(boss));
            }
        }

        async Task StepEggs(Player runner, SpawnedMonster boss, double now)
        {
            if (eggs.Count == 0) return;
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            for (int i = eggs.Count - 1; i >= 0; i--)
            {
                var egg = eggs[i];
                if (!runner.Area.Monsters.TryGetValue(egg.Id, out var em) || !Alive(em))
                {
                    eggs.RemoveAt(i);
                    continue;
                }
                bool close = players.Any(p => Distance(p.Walk.Position(now), egg.At) <= 7);
                if (!close && now < egg.DetonateAt) continue;
                eggs.RemoveAt(i);
                await RetireMonsters(runner, [egg.Id]);
                await SpawnTimedEffect(runner, effects, egg.At, EggExplodeArt, now + 1.2, scale: 1.2f);
                pools.Add(new(egg.At, now + 5.5, 8.0));
                var atk = ElementalSpellAttack(boss, DamageType.Chaos, 1.15, 11);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, egg.At) <= 11, () => !stopped && Alive(boss));
            }
        }

        async Task StepChaosGround(Player runner, SpawnedMonster boss, double now)
        {
            if (pools.Count == 0)
            {
                ResetChaosExposures(runner);
                return;
            }
            var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (22, 32);
            double chaosDps = (min + max) * 0.42;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = ChaosExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                    pools.Any(z => Distance(at, z.At) <= z.Radius)
                        ? DamagePacket.Of(DamageType.Chaos, chaosDps, chaosDps)
                        : DamagePacket.Empty,
                    () => !stopped && Alive(boss));
            }
        }

        static void ResetChaosExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (ChaosExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task Cleanup(Player runner, SpawnedMonster boss)
        {
            cleaned = true;
            cocooned = false;
            MarkPhaseCompleted(runner.Instance, PhaseKey, 3);
            SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            hits.Clear();
            pools.Clear();
            ResetChaosExposures(runner);
            await RetireMonsters(runner, eggs.Select(e => e.Id));
            eggs.Clear();
            await RetireMonsters(runner, adds);
            adds.Clear();
            var remainingNests = runner.Area.Monsters.Where(p => p.Value.Variety?.Id == Nest).Select(p => p.Key).ToArray();
            await RetireMonsters(runner, remainingNests);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Ryslatha defeated; nests, eggs, adds, and caustic pools cleared");
        }
    }
}

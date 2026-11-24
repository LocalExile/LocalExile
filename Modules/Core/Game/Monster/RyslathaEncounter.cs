using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>Drives Ryslatha after the three nests die and the quest spawns her at the arena marker.</summary>
public static class RyslathaEncounter
{
    public const string Area = "2_6_11";
    public const string Nest = "Metadata/Monsters/ParasiteNest/ParasiteNestRyslatha";         // MV 4939
    public const string Boss = "Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss";   // MV 1610
    public const string Egg = "Metadata/Monsters/SpiderlingEgg/RyslathaEgg";                  // MV 6083

    static readonly int[] BossEffects = [2069, 6266, 3432, 8449, 3434];

    const string SlideArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/powerslide/rig.ao";
    const string SlideGroundArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/powerslide/ground_rig.ao";
    const string EggExplodeArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/spit_parasite/parasiteeggexplodenew.ao";
    const string SpitArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/spit_parasite/rig.ao";
    const string FlameImpactArt = "Metadata/Effects/Spells/monsters_effects/act7/ryslatha/poison_flamethrower/impact/rig.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> ChaosExposures = new();

    // The quest engine forms the boss at this point after all three nests die.
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
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id == Boss))
                return;
            fight = Fights.GetValue(player.Instance, _ => new Fight());
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
            foreach (var pair in Fights) pair.Value.Stopped = true;
            foreach (var pair in ChaosExposures) pair.Value.Last = double.NaN;
        }
    }

    sealed record ChaosPool((int X, int Y) At, double Ends, double Radius);
    sealed record VolatileEgg(uint Id, (int X, int Y) At, double DetonateAt);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<ChaosPool> pools = [];
        readonly List<VolatileEgg> eggs = [];
        uint bossId;
        bool initialized, cleaned;
        public bool Stopped;
        double nextChaseAt, nextSlideAt, nextEggAt, nextBreathAt;

        public async Task Step(Player runner, double now)
        {
            if (Stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !Stopped);
            pools.RemoveAll(p => now >= p.Ends);

            var bossEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Boss);
            if (bossEntry.Value is null) return;
            var boss = bossEntry.Value;
            bossId = bossEntry.Key;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: bossId);
            if (target is null)
            {
                ResetChaosExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                boss.Aggro = true;
                await BindEffects(runner, bossId, boss, BossEffects);
                await EnsureBossBar(runner, bossId, boss);
                nextSlideAt = now + 5.5;
                nextEggAt = now + 4.5;
                nextBreathAt = now + 7.5;
                Console.WriteLine($"[gateway] Ryslatha controller active for 0x{bossId:x}");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, bossId, boss);
            }

            await StepEggs(runner, boss, now);
            await StepChaosGround(runner, boss, now);

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
                            ownedCurrent: () => !Stopped && !cleaned && Alive(boss));
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
                    await HitBeamLine(runner, bossId, boss, pos, angle, 34, 8.0, atk, () => !Stopped && Alive(boss));
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
                    await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, egg.At) <= 11, () => !Stopped && Alive(boss));
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
                    () => !Stopped && Alive(boss));
            }
        }

        static void ResetChaosExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (ChaosExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            hits.Clear();
            pools.Clear();
            ResetChaosExposures(runner);
            await RetireMonsters(runner, eggs.Select(e => e.Id));
            eggs.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Ryslatha defeated; eggs and caustic pools cleared");
        }
    }
}

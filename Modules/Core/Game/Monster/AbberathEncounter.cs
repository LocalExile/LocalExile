using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 6 — Prisoner's Gate (<c>2_6_8</c>) Abberath, the Cloven One 2-arena encounter:
/// <list type="bullet">
/// <item><b>Phase 1 (<c>Abberath[6008]</c> in <c>bossphase1.arm</c>)</b>: Leaves <c>AbberathsFury[6744]</c> flaming
/// hoof ground burns while moving, casts <c>AbberathMeteorUpheaval[6748]</c> and <c>AbberathLeapSlam[808]</c>. At
/// 50% HP casts <c>AbberathHide[6753]</c> (retreating rather than dying), unlocks <c>DoorAbberathPhase1</c>, spawns a
/// Goatmen canyon ambush (<c>GoatmanLeapSlamAbberath[237]</c> &amp; <c>GoatmanShamanAbberath[280]</c>,
/// <c>NoRewards = true</c>), and emerges in <c>bossphase2.arm</c> as <c>Abberath2[6012]</c>.</item>
/// <item><b>Phase 2 (<c>Abberath2[6012]</c> in <c>bossphase2.arm</c>)</b>: Sweeping
/// <c>AbberathHellfireBarrageA..E[6140..6144]</c> meteor curtains across the arena alongside flaming hooves,
/// upheaval, leap slam, and arena door management (<c>DoorAbberathPhase1</c>/<c>DoorAbberathPhase2</c>).</item>
/// </list>
/// </summary>
public static class AbberathEncounter
{
    public const string Area = "2_6_8";
    public const string Phase1 = "Metadata/Monsters/Abberath/Abberath";                       // MV 6008
    public const string Phase2 = "Metadata/Monsters/Abberath/Abberath2";                      // MV 6012
    public const string GoatLeaper = "Metadata/Monsters/Goatman/GoatmanLeapSlamAbberath";     // MV 237
    public const string GoatShaman = "Metadata/Monsters/Goatman/GoatmanShamanAbberath";       // MV 280

    const string PhaseKey = "Abberath";
    static readonly int[] Phase1Thresholds = [50];
    static readonly int[] Phase1Effects = [406, 6744, 6748, 6751, 6752, 6753, 808];
    static readonly int[] Phase2Effects = [406, 6744, 6748, 6751, 808, 6140, 6141, 6142, 6143, 6144];

    // Verified from game-files.zip .ot definitions:
    // DoorAbberathPhase1: start_blocked = true (state 1 = locked, state 2 = unlocked)
    // DoorAbberathPhase2: start_blocked = false (state 2 = locked, state 1 = unlocked)
    const string DoorPhase1Type = "Metadata/Terrain/Act6/Area8/Objects/DoorAbberathPhase1";
    const string DoorPhase2Type = "Metadata/Terrain/Act6/Area8/Objects/DoorAbberathPhase2";

    const string HoofArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/abberathenragedhoof.ao";
    const string JumpOutArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/abberathjumpout.ao";
    const string CliffBombArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/cliffbomb_01.ao";
    const string FireballBigArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/combofireballbig.ao";
    const string FireballSmallArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/combofireballsmall.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> BurnExposures = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id is Phase1 or Phase2))
                return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player));
        }
        await fight.Gate.WaitAsync();
        try
        {
            SyncGatesForPlayer(player, fight.AllGates);
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static Task Leaving(Player player)
    {
        if (BurnExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        if (Fights.TryGetValue(player.Instance, out var fight))
            ReleaseGatesForPlayer(player, fight.AllGates);
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in BurnExposures) pair.Value.Last = double.NaN;
            ReleaseAllGatesOnUnload();
        }
    }

    sealed record BurnPatch((int X, int Y) At, double Ends, double Radius);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<GlyphQuest.Placement> phase1Doors;
        readonly List<GlyphQuest.Placement> phase2Doors;
        readonly List<(int X, int Y)> goatSpawners;
        readonly List<(int X, int Y)> p2GoatSpawners;
        readonly List<(int X, int Y)> meteorMarkers;
        readonly List<(int X, int Y)> arenaMiddles;

        readonly List<ArenaGate> p1Gates = [];
        readonly List<ArenaGate> p2Gates = [];
        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<BurnPatch> hoofBurns = [];
        readonly HashSet<uint> adds = [];

        public IEnumerable<ArenaGate> AllGates => p1Gates.Concat(p2Gates);

        uint p1Id, p2Id;
        (int X, int Y) lastBossPos;
        bool gatesInit, p1Init, p1Retreated, p2Init, cleaned, stopped;
        double nextChaseAt, nextHoofAt, nextUpheavalAt, nextLeapAt, nextHellfireAt, nextAddAt;
        int hellfirePattern;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            phase1Doors = placed.Of(DoorPhase1Type);
            phase2Doors = placed.Of(DoorPhase2Type);
            goatSpawners = [.. placed.Of("Metadata/Monsters/Abberath/BossGoatSpawner")
                .Concat(placed.Of("Metadata/Monsters/Abberath/BossGoatSpawnerCaster")).Select(p => (p.X, p.Y))];
            p2GoatSpawners = [.. placed.Of("Metadata/Monsters/Abberath/BossGoatSpawnerPhase2")
                .Concat(placed.Of("Metadata/Monsters/Abberath/BossGoatSpawnerCasterPhase2")).Select(p => (p.X, p.Y))];
            meteorMarkers = [.. Enumerable.Range(1, 5)
                .SelectMany(i => placed.Of($"Metadata/Monsters/Abberath/MeteorShowerMarker{i}Target"))
                .Select(p => (p.X, p.Y))];
            arenaMiddles = [.. placed.Of("Metadata/Monsters/Abberath/ArenaMiddle").Select(p => (p.X, p.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (p1Id != 0 && area.Monsters.TryGetValue(p1Id, out var m1))
                ClearInvulnerableOnUnload(area, p1Id, m1, CannotBeDamagedBuff);
            if (p2Id != 0 && area.Monsters.TryGetValue(p2Id, out var m2))
                ClearInvulnerableOnUnload(area, p2Id, m2, CannotBeDamagedBuff);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            hoofBurns.RemoveAll(b => now >= b.Ends);

            if (!gatesInit)
            {
                gatesInit = true;
                bool alreadyRetreated = runner.Instance.HasState(PhaseFlag(PhaseKey, 0));
                p1Retreated = alreadyRetreated;
                foreach (var gp in phase1Doors)
                    if (await CreateGate(runner, DoorPhase1Type, gp, startLocked: !alreadyRetreated, lockedState: 1, unlockedState: 2) is { } g)
                        p1Gates.Add(g);
                foreach (var gp in phase2Doors)
                    if (await CreateGate(runner, DoorPhase2Type, gp, startLocked: false, lockedState: 2, unlockedState: 1) is { } g)
                        p2Gates.Add(g);
            }

            var p1Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Phase1);
            var p2Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Phase2);

            if (p1Entry.Value is { } p1)
            {
                p1Id = p1Entry.Key;
                if (Alive(p1))
                    await StepPhase1(runner, p1, now);
                else if (!p1Retreated)
                    await CompletePhase1Retreat(runner, p1, p1.Walk.Position(now), now, castHide: false);
            }

            if (p2Entry.Value is { } p2)
            {
                p2Id = p2Entry.Key;
                if (Alive(p2))
                    await StepPhase2(runner, p2, now);
                else if (!cleaned)
                    await Cleanup(runner);
            }

            await StepBurnGround(runner, p2Entry.Value ?? p1Entry.Value, now);
        }

        async Task StepPhase1(Player runner, SpawnedMonster boss, double now)
        {
            SyncSavedPhases(runner.Instance, boss, PhaseKey, Phase1Thresholds);
            var pos = boss.Walk.Position(now);
            // The Valley of the Fire Drinker entrance sits ~132 cells east of mapboss1WG; engage across the arena.
            var target = NearestLivingTarget(runner, pos, now, maxRange: 160, awakeId: p1Id);
            if (target is null) return;

            if (!p1Init)
            {
                p1Init = true;
                lastBossPos = pos;
                boss.Aggro = true;
                boss.NoRewards = true;
                MarkStarted(runner.Instance, PhaseKey);
                await BindEffects(runner, p1Id, boss, Phase1Effects);
                await EnsureBossBar(runner, p1Id, boss);
                nextHoofAt = now + 0.8;
                nextUpheavalAt = now + 4.5;
                nextLeapAt = now + 7.5;
                Console.WriteLine($"[gateway] Abberath Phase 1 controller active for 0x{p1Id:x}");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, p1Id, boss);
            }

            // 50% HP Canyon Retreat: cast AbberathHide[6753], open DoorAbberathPhase1, spawn Goatmen ambush, form Abberath2 in bossphase2.arm
            if (!p1Retreated && boss.LifePhases is { Completed: 0 } ph && ph.Begin(boss.Life, boss.MaxLife))
            {
                ph.Finish();
                await CompletePhase1Retreat(runner, boss, pos, now, castHide: true);
                return;
            }

            await LeaveHoofTrail(runner, p1Id, boss, pos, now);
            if (!CanAct(boss, now)) return;
            await StepCommonAbberathSkills(runner, p1Id, boss, target, phase2: false, now);
        }

        async Task CompletePhase1Retreat(Player runner, SpawnedMonster boss, (int X, int Y) pos, double now, bool castHide)
        {
            p1Retreated = true;
            MarkPhaseCompleted(runner.Instance, PhaseKey, 0);
            if (castHide)
            {
                SetInvulnerable(runner, p1Id, boss, true, CannotBeDamagedBuff);
                await CastAction(runner, p1Id, boss, 6753, pos, 1.0, now, "[Abberath] Hide & retreat");
                await SpawnTimedEffect(runner, effects, pos, JumpOutArt, now + 1.6, scale: 1.3f);
            }

            // Unlock canyon exit door
            foreach (var g in p1Gates) await SetGateLocked(runner, g, false);

            // Spawn Goatmen canyon ambush (NoRewards = true)
            await SpawnGoatAmbush(runner, boss.Level, pos, goatSpawners, count: 5);

            // Spawn Abberath2 in bossphase2.arm if not already present
            if (!runner.Area.Monsters.Values.Any(m => m.Variety?.Id == Phase2))
            {
                var p2Spot = PickPhase2Spot(runner, pos);
                if (Monsters.Find(Phase2) is { Supported: true, Id: Phase2 } v2)
                {
                    await SpawnMonster(runner, v2, boss.Level, MonsterRarity.Unique, p2Spot.X, p2Spot.Y,
                        lifePercent: 100);
                }
            }

            if (castHide)
            {
                await RetireMonsters(runner, [p1Id]);
                p1Id = 0;
            }
            Console.WriteLine("[gateway] Abberath Phase 1 retreated into canyon; DoorAbberathPhase1 opened & Goatmen ambush spawned");
        }

        async Task StepPhase2(Player runner, SpawnedMonster boss, double now)
        {
            var pos = boss.Walk.Position(now);
            // The Valley of the Soul Drinker entrance sits ~162 cells west of mapboss2WG; engage across the arena.
            var target = NearestLivingTarget(runner, pos, now, maxRange: 165, awakeId: p2Id);
            if (target is null)
            {
                foreach (var g in p2Gates) await SetGateLocked(runner, g, false);
                return;
            }

            if (!p2Init)
            {
                p2Init = true;
                lastBossPos = pos;
                boss.Aggro = true;
                await BindEffects(runner, p2Id, boss, Phase2Effects);
                await EnsureBossBar(runner, p2Id, boss);
                nextHoofAt = now + 0.8;
                nextUpheavalAt = now + 4.0;
                nextLeapAt = now + 7.0;
                nextHellfireAt = now + 5.5;
                nextAddAt = now + 11.0;
                Console.WriteLine($"[gateway] Abberath Phase 2 controller active for 0x{p2Id:x}");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, p2Id, boss);
            }

            foreach (var g in p2Gates) await SetGateLocked(runner, g, true);
            await LeaveHoofTrail(runner, p2Id, boss, pos, now);
            if (!CanAct(boss, now)) return;

            // Phase 2 sweeping AbberathHellfireBarrageA..E [6140..6144]
            if (now >= nextHellfireAt)
            {
                nextHellfireAt = now + 9.5;
                int effect = 6140 + (hellfirePattern++ % 5);
                var aim = target.Walk.Position(now);
                if (await CastAction(runner, p2Id, boss, effect, aim, 1.7, now, $"[Abberath2] Hellfire Barrage {effect}") is { } gen)
                {
                    var atk = ElementalSpellAttack(boss, DamageType.Fire, 1.15, 11);
                    double sweepAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    double perpX = -Math.Sin(sweepAngle), perpY = Math.Cos(sweepAngle);
                    for (int i = -2; i <= 2; i++)
                    {
                        var drop = meteorMarkers.Count >= 5
                            ? meteorMarkers[(i + 2) % meteorMarkers.Count]
                            : WalkableNear(runner, (
                                (int)Math.Round(aim.X + perpX * (i * 9)),
                                (int)Math.Round(aim.Y + perpY * (i * 9))));
                        await SpawnTimedEffect(runner, effects, drop, FireballSmallArt, now + 0.9 + (i + 2) * 0.22, scale: 1.15f);
                        hits.Add(new(now + 0.85 + (i + 2) * 0.22, p2Id, boss, drop, 10.5, atk, gen, CliffBombArt, 1.15f));
                    }
                    return;
                }
            }

            if (now >= nextAddAt)
            {
                nextAddAt = now + 16.0;
                int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (living < 4)
                    await SpawnGoatAmbush(runner, boss.Level, pos, p2GoatSpawners, count: 2);
            }

            await StepCommonAbberathSkills(runner, p2Id, boss, target, phase2: true, now);
        }

        async Task StepCommonAbberathSkills(Player runner, uint id, SpawnedMonster boss, Player target, bool phase2, double now)
        {
            var pos = boss.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // AbberathLeapSlam[808]
            if (now >= nextLeapAt && dist is >= 14 and <= 54)
            {
                nextLeapAt = now + (phase2 ? 8.5 : 10.0);
                var landing = WalkableNear(runner, aim);
                if (await CastAction(runner, id, boss, 808, landing, 1.25, now, "[Abberath] Leap Slam") is { } gen)
                {
                    boss.Walk.Teleport(landing.X, landing.Y, now);
                    await SendWorldRecord(runner, id, InstanceProtocol.Teleport(id, landing.X, landing.Y), null);
                    await SpawnTimedEffect(runner, effects, landing, JumpOutArt, now + 1.1, scale: 1.15f);
                    hits.Add(new(now + 0.7, id, boss, landing, 15,
                        HybridAttack(boss, DamageType.Fire, 0.9, 0.75, 15), gen, FireballBigArt, 1.25f));
                    return;
                }
            }

            // AbberathMeteorUpheaval[6748]
            if (now >= nextUpheavalAt && dist <= 52)
            {
                nextUpheavalAt = now + (phase2 ? 6.0 : 7.5);
                if (await CastAction(runner, id, boss, 6748, aim, 1.35, now, "[Abberath] Meteor Upheaval") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = ElementalSpellAttack(boss, DamageType.Fire, 1.05, 11);
                    for (int s = 1; s <= 4; s++)
                    {
                        var burst = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(angle) * (s * 11)),
                            (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 11))));
                        await SpawnTimedEffect(runner, effects, burst, FireballSmallArt, now + 0.55 + s * 0.2, scale: 1.0f);
                        hits.Add(new(now + 0.5 + s * 0.2, id, boss, burst, 10, atk, gen, CliffBombArt, 1.05f));
                    }
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, id, boss, target, now, nextChaseAt, hits, meleeScale: 1.05);
        }

        async Task LeaveHoofTrail(Player runner, uint id, SpawnedMonster boss, (int X, int Y) pos, double now)
        {
            if (now < nextHoofAt || Distance(pos, lastBossPos) < 4) return;
            lastBossPos = pos;
            nextHoofAt = now + 0.75;
            if (hoofBurns.Count >= 14) return;
            await SpawnTimedEffect(runner, effects, pos, HoofArt, now + 4.5, scale: 0.95f);
            hoofBurns.Add(new(pos, now + 4.5, 7.5));
            // Abberath's Fury fiery step explosion
            var atk = ElementalSpellAttack(boss, DamageType.Fire, 0.55, 8);
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
                await ArenaHazards.Hit(p, id, boss, atk, at => Distance(at, pos) <= 8, () => !stopped && Alive(boss));
        }

        async Task StepBurnGround(Player runner, SpawnedMonster? source, double now)
        {
            if (hoofBurns.Count == 0 || source is null)
            {
                foreach (var p in ActivePlayers(runner))
                    if (BurnExposures.TryGetValue(p, out var e)) e.Last = double.NaN;
                return;
            }
            var (min, max) = Monsters.BaseDamage(source.Variety!, source.Level) ?? (25, 35);
            double burnDps = (min + max) * 0.45;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = BurnExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                    hoofBurns.Any(b => Distance(at, b.At) <= b.Radius)
                        ? DamagePacket.Of(DamageType.Fire, burnDps, burnDps)
                        : DamagePacket.Empty,
                    () => !stopped);
            }
        }

        (int X, int Y) PickPhase2Spot(Player runner, (int X, int Y) p1Pos)
        {
            if (runner.LaterFormPoints.TryGetValue(Phase2, out var formPt))
                return WalkableNear(runner, formPt);
            var farthest = arenaMiddles.OrderByDescending(m => Distance(m, p1Pos)).FirstOrDefault();
            if (farthest != default && Distance(farthest, p1Pos) > 25)
                return WalkableNear(runner, farthest);
            return RingSpot(runner, p1Pos, 0, 20);
        }

        async Task SpawnGoatAmbush(Player runner, byte level, (int X, int Y) fallback, List<(int X, int Y)> spawners, int count)
        {
            var rng = AreaRng(runner);
            for (int i = 0; i < count; i++)
            {
                var spot = spawners.Count > 0
                    ? spawners[rng.Next(spawners.Count)]
                    : RingSpot(runner, fallback, rng.NextDouble() * Math.Tau, 18);
                string kind = (i % 3) == 0 ? GoatShaman : GoatLeaper;
                await SpawnAdd(runner, kind, level, spot, p2Id != 0 ? p2Id : p1Id, adds,
                    ownedCurrent: () => !stopped && !cleaned);
            }
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            hits.Clear();
            hoofBurns.Clear();
            foreach (var g in p1Gates) await SetGateLocked(runner, g, false);
            foreach (var g in p2Gates) await SetGateLocked(runner, g, false);
            await RetireMonsters(runner, adds);
            adds.Clear();
            await ClearAllEffects(runner, effects);
            foreach (var p in ActivePlayers(runner))
                if (BurnExposures.TryGetValue(p, out var e)) e.Last = double.NaN;
            Console.WriteLine("[gateway] Abberath defeated; canyon doors unlocked and hazards cleared");
        }
    }
}

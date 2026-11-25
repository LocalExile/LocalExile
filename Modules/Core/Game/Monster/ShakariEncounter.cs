using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 9 — The Oasis (<c>2_9_4</c>) Shakari, Queen of the Sands encounter controller:
/// <list type="bullet">
/// <item><b>Phase 1 (<c>Shakari[6275]</c> in <c>boss1.arm</c>)</b>: Desert melee, caustic tail strikes
/// (<c>ShakariTailStrike[8768]</c>), sand upheaval (<c>ShakariUpheaval[8769]</c>), burrow lunges
/// (<c>ShakariTrailBurrow[8876]</c>), and <c>ShakariQuicksandDaemon</c> slowing/damaging pools. At 65% HP
/// (coordinated with <c>Act9.cs</c>'s <c>On.Threshold</c>/<c>On.Forms</c>), retreats through the desert canyon
/// spawning quicksand hazards and <c>NoRewards</c> scorpion ambushes into <c>boss2.arm</c>.</item>
/// <item><b>Phase 2 (<c>Shakari2[6278]</c> in <c>boss2.arm</c>)</b>: <c>[66, 33]</c> burrow intermissions
/// (<c>WorldHidden</c> + <c>SetInvulnerable</c>) with shrinking blinding/damaging sandstorm ring,
/// <c>Tamulus[6145]</c> / <c>Tamipin[6149]</c> + scorpion waves (<c>NoRewards = true</c>), caustic tail strikes,
/// and <c>ShakariTrailBurrow2[8877]</c> ambush lunges.</item>
/// </list>
/// </summary>
public static class ShakariEncounter
{
    public const string Area = "2_9_4";
    sealed record GateCheckpoint(InstanceState Instance, (int X, int Y) Position);
    static readonly RuleTable<Player, GateCheckpoint> GateCheckpoints = new();

    internal static void RememberGateCheckpoint(Player player, AreaTransitionObject gate, (int X, int Y) arrival)
    {
        if (player.Layout.Id == Area && gate.TeleportTo is not null && gate.Feature is null
            && gate.DestinationHash16 == player.Layout.Hash)
            GateCheckpoints.AddOrUpdate(player, new(player.Instance, arrival));
    }

    internal static (int X, int Y)? CheckpointOf(Player player) =>
        player.Layout.Id == Area && GateCheckpoints.TryGetValue(player, out var checkpoint)
            && ReferenceEquals(checkpoint.Instance, player.Instance)
            && player.Layout.WalkableFor(checkpoint.Position.X, checkpoint.Position.Y, 1)
            ? checkpoint.Position : null;

    public const string ShakariP1 = "Metadata/Monsters/Shakari/Shakari";                     // [6275]
    public const string ShakariP2 = "Metadata/Monsters/Shakari/Shakari2";                    // [6278]
    public const string Tamulus = "Metadata/Monsters/Scorpion/YellowScorpionLargeBossShakari"; // [6145]
    public const string Tamipin = "Metadata/Monsters/Scorpion/BlackScorpionLargeBossShakari_"; // [6149]
    public const string BlackScorpion = "Metadata/Monsters/Scorpion/BlackScorpionShakari";   // [6141]
    public const string SandScorpion = "Metadata/Monsters/Scorpion/YellowScorpionShakari__"; // [6144]
    public const string PredatoryScorpion = "Metadata/Monsters/Scorpion/BlackScorpionLargeShakari"; // [6148]

    // Verified GrantedEffects rows on Shakari[6275] and Shakari2[6278] in client.db:
    const int MeleeEffect = 406;
    const int TailStrikeEffect = 8768;       // ShakariTailStrike
    const int UpheavalEffect = 8769;         // ShakariUpheaval
    const int BurrowEffect = 8802;           // ShakariBurrow
    const int TrailBurrowP1Effect = 8876;    // ShakariTrailBurrow
    const int TrailBurrowP2Effect = 8877;    // ShakariTrailBurrow2
    const int SlamEffect = 5602;             // GAShakariSlam

    static readonly int[] P1Effects = [MeleeEffect, TailStrikeEffect, UpheavalEffect, BurrowEffect, TrailBurrowP1Effect, SlamEffect];
    static readonly int[] P2Effects = [MeleeEffect, TailStrikeEffect, UpheavalEffect, BurrowEffect, TrailBurrowP2Effect, SlamEffect];

    // Verified BuffDefinitions rows in client.db:
    const ushort CannotBeDamagedBuff = 350;  // cannot_be_damaged
    const ushort BlindedBuff = 179;          // blinded
    const ushort QuicksandBuff = 946;        // quicksand_display_buff
    const ushort CausticCloudBuff = 484;     // caustic_cloud

    // Verified .ao visual paths in game-files.zip:
    const string QuicksandArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/quick_sand/rig.ao";
    const string QuicksandRippleArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/quick_sand_ripple/quicksandripple.ao";
    const string BurrowSubmergeArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/burrow/burrow_submerge_01.ao";
    const string BurrowEmergeArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/burrow/burrow_emerge_01.ao";
    const string BurrowTrailArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/burrow/travelling.ao";
    const string TailStrikeArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/tail_strike_epic/impact.ao";
    const string SandSpikeArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/multi_slam/sandspike1.ao";
    const string SandstormArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/sandstorm/rig.ao";
    const string CausticGroundArt = "Metadata/Effects/Spells/ground_effects/caustic/caustic_01.ao";

    const string PhaseKey = "ShakariP2";
    static readonly int[] P2Thresholds = [66, 33];

    static readonly IReadOnlyDictionary<uint, int> QuicksandSlowStats = new Dictionary<uint, int>
    {
        [Stat.BaseMovementVelocityPlusPct] = -38,
    };

    static readonly IReadOnlyDictionary<uint, int> SandstormBlindStats = new Dictionary<uint, int>
    {
        [Stat.BaseMovementVelocityPlusPct] = -20,
    };

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id is ShakariP1 or ShakariP2);
            if (!present) return;
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

    public static async Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return;
        if (Fights.TryGetValue(player.Instance, out var fight))
            await fight.ReleasePlayerDebuffs(player);
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
        }
    }

    sealed record PoolHazard((int X, int Y) At, double Radius, double Until, bool IsCaustic);
    sealed record DelayedHit(double At, int Gen, uint SourceId, SpawnedMonster Source, MonsterAttack Attack, (int X, int Y) Spot, double Radius, string? BurstAo);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<PoolHazard> pools = [];
        readonly List<DelayedHit> hits = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> canyonAdds = [];
        readonly HashSet<uint> p2WaveAdds = [];
        readonly Dictionary<Player, ArenaHazards.Exposure> groundExposures = [];
        readonly object quicksandHolder = new();
        readonly object sandstormHolder = new();
        readonly object causticHolder = new();

        readonly (int X, int Y)? cachedBoss2Middle;
        readonly List<(int X, int Y)> canyonQuicksandSpots;
        readonly List<(int X, int Y)> boss2QuicksandSpots;
        readonly List<(int X, int Y)> canyonScorpionSpawners;
        readonly List<(int X, int Y)> boss2ScorpionSpawners;

        (int X, int Y) sandPitCenter;
        uint p1BossId, p2BossId;
        bool p1Initialized, canyonSpawned, p2Initialized, p2Burrowed, p2Cleaned, stopped;
        double p2BurrowEndsAt, nextAmbushLungeAt;
        double nextP1Special, nextP1Pool;
        double nextP2Special, nextP2Pool, nextDebuffTick;
        double sandstormSafeRadius = 999;
        bool sandstormActive;
        int rotation;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            cachedBoss2Middle = placed.Of("Metadata/Monsters/Shakari/ArenaMiddle").FirstOrDefault() is { } mid ? (mid.X, mid.Y) : null;
            canyonQuicksandSpots = [.. placed.Of("Metadata/Monsters/Daemon/ShakariQuicksandDaemon").Select(p => (p.X, p.Y))];
            boss2QuicksandSpots = [.. placed.Of("Metadata/Monsters/Shakari/ShakariQuicksandSpawnerBoss")
                .Concat(placed.Of("Metadata/Monsters/Shakari/ShakariQuicksandSpawnerBossMiddle"))
                .Concat(placed.Of("Metadata/Monsters/Daemon/ShakariQuicksandDaemon2_"))
                .Select(p => (p.X, p.Y))];
            canyonScorpionSpawners = [.. placed.Of("Metadata/Monsters/Shakari/ShakariScorpionSpawner")
                .Concat(placed.Of("Metadata/Monsters/Shakari/ShakariScorpionLargeSpawner"))
                .Select(p => (p.X, p.Y))];
            boss2ScorpionSpawners = [.. placed.Of("Metadata/Monsters/Shakari/ShakariScorpionSpawnerBoss")
                .Concat(placed.Of("Metadata/Monsters/Shakari/ShakariScorpionLargeSpawnerBoss"))
                .Select(p => (p.X, p.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (p2BossId != 0 && area.Monsters.TryGetValue(p2BossId, out var b2))
            {
                ClearInvulnerableOnUnload(area, p2BossId, b2, CannotBeDamagedBuff);
                b2.WorldHidden = false;
            }
            foreach (var p in groundExposures.Keys.ToArray())
            {
                _ = ReleasePlayerDebuff(p, quicksandHolder);
                _ = ReleasePlayerDebuff(p, sandstormHolder);
                _ = ReleasePlayerDebuff(p, causticHolder);
            }
            groundExposures.Clear();
        }

        public async Task ReleasePlayerDebuffs(Player p)
        {
            groundExposures.Remove(p);
            await ReleasePlayerDebuff(p, quicksandHolder);
            await ReleasePlayerDebuff(p, sandstormHolder);
            await ReleasePlayerDebuff(p, causticHolder);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            pools.RemoveAll(p => now >= p.Until);
            await StepDelayedHits(runner, now);

            var p1Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == ShakariP1);
            if (p1Entry.Value is { } p1 && Alive(p1))
                await StepPhase1(runner, p1Entry.Key, p1, now);
            else if (p1Initialized && !canyonSpawned)
                await TriggerCanyonChase(runner, p1Entry.Value?.Walk.Position(now) ?? sandPitCenter, p1Entry.Value?.Level ?? 63, now);

            var p2Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == ShakariP2);
            if (p2Entry.Value is { } p2)
            {
                if (Alive(p2))
                    await StepPhase2(runner, p2Entry.Key, p2, now);
                else if (!p2Cleaned)
                    await Cleanup(runner);
            }

            if (!p2Cleaned)
                await StepGroundsAndSandstorm(runner, now);
        }

        async Task StepPhase1(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            p1BossId = bossId;
            boss.NoRewards = true;
            await EnsureNativeSkills(runner, bossId, boss, P1Effects, boundBosses);
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;

            if (!p1Initialized)
            {
                p1Initialized = true;
                sandPitCenter = cachedBoss2Middle ?? pos;
                nextP1Special = now + 2.2;
                nextP1Pool = now + 4.5;
            }

            // Trigger desert canyon chase hazards as Shakari reaches her 65% retreat threshold.
            if (!canyonSpawned && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 68)
                await TriggerCanyonChase(runner, pos, boss.Level, now);

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            if (now >= nextP1Pool)
            {
                nextP1Pool = now + 7.5;
                await SpawnQuicksandPool(runner, aim, 11, now + 11);
            }

            if (dist > 28 && now < nextP1Special)
            {
                await Chase(runner, bossId, boss, aim, now, "Shakari P1");
                return;
            }

            if (now >= nextP1Special)
            {
                int step = rotation++;
                int effect = step % 3 == 0 ? TailStrikeEffect : step % 3 == 1 ? UpheavalEffect : TrailBurrowP1Effect;
                nextP1Special = now + 4.2;
                await CastShakariSkill(runner, bossId, boss, P1Effects, effect, aim, now, "Shakari P1");
                return;
            }

            if (dist <= 16)
            {
                if (await CastAction(runner, bossId, boss, P1Effects, MeleeEffect, aim, now, DamageType.Physical, 1.0, 14, "Shakari P1", forceSpell: false) is { } cast)
                    hits.Add(new(now + cast.Attack.Seconds * 0.45, cast.Generation, bossId, boss, cast.Attack, aim, 13, null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "Shakari P1");
            }
        }

        async Task TriggerCanyonChase(Player runner, (int X, int Y) retreatFrom, byte level, double now)
        {
            canyonSpawned = true;
            var rng = AreaRng(runner);
            await SpawnTimedEffect(runner, effects, retreatFrom, BurrowSubmergeArt, now + 2.5, scale: 1.25f);

            // Activate quicksand daemon pools along the canyon path.
            if (canyonQuicksandSpots.Count > 0)
            {
                foreach (var spot in canyonQuicksandSpots.Take(6))
                    await SpawnQuicksandPool(runner, spot, 12, now + 35);
            }
            else
            {
                for (int i = 0; i < 4; i++)
                    await SpawnQuicksandPool(runner, RingSpot(runner, retreatFrom, i * (Math.Tau / 4.0), 18 + i * 6), 11, now + 30);
            }

            // Spawn scorpion canyon ambush waves (NoRewards = true).
            int count = Math.Min(6, Math.Max(4, canyonScorpionSpawners.Count));
            for (int i = 0; i < count; i++)
            {
                var spot = canyonScorpionSpawners.Count > 0
                    ? canyonScorpionSpawners[i % canyonScorpionSpawners.Count]
                    : RingSpot(runner, retreatFrom, rng.NextDouble() * Math.Tau, 16 + i * 5);
                string mob = i % 3 == 0 ? PredatoryScorpion : (i & 1) == 0 ? BlackScorpion : SandScorpion;
                await SpawnAdd(runner, mob, level, spot, p1BossId, canyonAdds, ownedCurrent: () => !stopped && !p2Cleaned);
            }
            Console.WriteLine($"[gateway] Shakari P1 retreated into canyon: spawned {canyonAdds.Count} scorpion ambush adds & quicksand pools");
        }

        async Task StepPhase2(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            bool rebound = p2BossId != bossId;
            p2BossId = bossId;
            await EnsureNativeSkills(runner, bossId, boss, P2Effects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, PhaseKey, P2Thresholds);
            var phases = boss.LifePhases!;

            if (!p2Initialized || rebound)
            {
                if (!p2Initialized)
                {
                    p2Initialized = true;
                    sandPitCenter = cachedBoss2Middle ?? boss.Walk.Position(now);
                    // If Act9.cs formed Shakari2 at P1's boss1 location and boss2 ArenaMiddle is available nearby, keep her at Sand Pit.
                    if (cachedBoss2Middle is { } mid && Distance(boss.Walk.Position(now), mid) > 55)
                        await TeleportBoss(runner, bossId, boss, mid, now, "[Shakari P2] relocated to boss2 Sand Pit");
                    nextP2Special = now + 2.5;
                    nextP2Pool = now + 4.0;
                    sandstormSafeRadius = phases.Completed >= 2 ? 26 : phases.Completed == 1 ? 36 : 48;
                }
                if (p2Burrowed)
                {
                    SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                    await SetWorldHidden(runner, bossId, boss, true);
                }
                Console.WriteLine($"[gateway] Shakari P2 controller synced for 0x{bossId:x} (phases done={phases.Completed}/2)");
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, p2Burrowed ? sandPitCenter : pos, now, maxRange: 145, awakeId: p2Burrowed ? null : bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;
            MarkStarted(runner.Instance, PhaseKey);

            // Handle active [66, 33] burrow intermission.
            if (p2Burrowed)
            {
                await StepBurrowIntermission(runner, bossId, boss, target, now);
                return;
            }

            // Safety guarantee: never leave Shakari2 invulnerable outside a burrow phase.
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);

            if (phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                await BeginBurrowIntermission(runner, bossId, boss, phases.Completed, now);
                return;
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            if (now >= nextP2Pool)
            {
                nextP2Pool = now + 6.5;
                var poolSpot = boss2QuicksandSpots.Count > 0 && AreaRng(runner).Next(2) == 0
                    ? boss2QuicksandSpots[AreaRng(runner).Next(boss2QuicksandSpots.Count)]
                    : aim;
                await SpawnQuicksandPool(runner, poolSpot, 11, now + 12);
            }

            if (dist > 30 && now < nextP2Special)
            {
                await Chase(runner, bossId, boss, aim, now, "Shakari P2");
                return;
            }

            if (now >= nextP2Special)
            {
                int step = rotation++;
                int effect = step % 4 == 0 ? TailStrikeEffect
                    : step % 4 == 1 ? TrailBurrowP2Effect
                    : step % 4 == 2 ? UpheavalEffect
                    : SlamEffect;
                nextP2Special = now + (phases.Completed >= 2 ? 3.2 : 4.0);
                await CastShakariSkill(runner, bossId, boss, P2Effects, effect, aim, now, "Shakari P2");
                return;
            }

            if (dist <= 16)
            {
                if (await CastAction(runner, bossId, boss, P2Effects, MeleeEffect, aim, now, DamageType.Physical, 1.05, 14, "Shakari P2", forceSpell: false) is { } cast)
                    hits.Add(new(now + cast.Attack.Seconds * 0.45, cast.Generation, bossId, boss, cast.Attack, aim, 13, null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "Shakari P2");
            }
        }

        async Task BeginBurrowIntermission(Player runner, uint bossId, SpawnedMonster boss, int phaseIdx, double now)
        {
            p2Burrowed = true;
            sandstormActive = true;
            sandstormSafeRadius = phaseIdx == 0 ? 34 : 24;
            p2BurrowEndsAt = now + 18;
            nextAmbushLungeAt = now + 3.5;
            hits.Clear();

            var pos = boss.Walk.Position(now);
            await SpawnTimedEffect(runner, effects, pos, BurrowSubmergeArt, now + 2.2, scale: 1.3f);
            await SpawnTimedEffect(runner, effects, sandPitCenter, SandstormArt, now + 18.0, scale: 1.5f);
            SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
            await SetWorldHidden(runner, bossId, boss, true);

            // Spawn miniboss (Tamulus at 66%, Tamipin at 33%) + scorpion wave, all NoRewards = true.
            string miniBoss = phaseIdx == 0 ? Tamulus : Tamipin;
            var rng = AreaRng(runner);
            var miniSpot = boss2ScorpionSpawners.Count > 0
                ? boss2ScorpionSpawners[0]
                : RingSpot(runner, sandPitCenter, rng.NextDouble() * Math.Tau, 16);
            await SpawnAdd(runner, miniBoss, boss.Level, miniSpot, bossId, p2WaveAdds,
                rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !p2Cleaned);

            for (int i = 0; i < 4; i++)
            {
                var spot = boss2ScorpionSpawners.Count > 1
                    ? boss2ScorpionSpawners[(i + 1) % boss2ScorpionSpawners.Count]
                    : RingSpot(runner, sandPitCenter, i * (Math.Tau / 4.0), 18);
                string scorp = i == 0 ? PredatoryScorpion : (i & 1) == 0 ? BlackScorpion : SandScorpion;
                await SpawnAdd(runner, scorp, boss.Level, spot, bossId, p2WaveAdds,
                    ownedCurrent: () => !stopped && !p2Cleaned);
            }
            Console.WriteLine($"[gateway] Shakari P2 burrow intermission {phaseIdx + 1}/2: spawned {miniBoss} + scorpion wave, sandstorm safe radius={sandstormSafeRadius}");
        }

        async Task StepBurrowIntermission(Player runner, uint bossId, SpawnedMonster boss, Player target, double now)
        {
            // Periodically shrink the safe eye slightly and erupt ShakariTrailBurrow2[8877] ambush lunges under players.
            if (now >= nextAmbushLungeAt)
            {
                nextAmbushLungeAt = now + 4.2;
                var aim = target.Walk.Position(now);
                await SpawnTimedEffect(runner, effects, aim, BurrowTrailArt, now + 1.5, scale: 1.2f);
                var ambushAtk = BuildAttack(runner, boss, P2Effects, TrailBurrowP2Effect, DamageType.Chaos, 1.25, 13, forceSpell: false);
                hits.Add(new(now + 1.15, boss.ActionGeneration, bossId, boss, ambushAtk, aim, 13, BurrowEmergeArt));
            }

            bool addsAlive = p2WaveAdds.Any(aid => runner.Area.Monsters.TryGetValue(aid, out var m) && Alive(m));
            if (addsAlive && now < p2BurrowEndsAt) return;

            p2Burrowed = false;
            p2WaveAdds.Clear();
            var emergeSpot = RingSpot(runner, target.Walk.Position(now), AreaRng(runner).NextDouble() * Math.Tau, 10);
            await TeleportBoss(runner, bossId, boss, emergeSpot, now);
            await SetWorldHidden(runner, bossId, boss, false);
            SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            await SpawnTimedEffect(runner, effects, emergeSpot, BurrowEmergeArt, now + 2.0, scale: 1.3f);
            MarkPhaseCompleted(runner.Instance, PhaseKey, boss.LifePhases!.Completed);
            boss.LifePhases.Finish();
            boss.BusyUntil = now + 1.0;
            Console.WriteLine($"[gateway] Shakari P2 resurfaced after burrow intermission {boss.LifePhases.Completed}/2");
        }

        async Task CastShakariSkill(Player runner, uint bossId, SpawnedMonster boss, int[] effectsList, int effect, (int X, int Y) aim, double now, string tag)
        {
            var pos = boss.Walk.Position(now);
            DamageType elem = effect == TailStrikeEffect ? DamageType.Chaos : DamageType.Physical;
            double scale = effect == SlamEffect ? 1.5 : effect == TailStrikeEffect ? 1.3 : 1.2;
            if (await CastAction(runner, bossId, boss, effectsList, effect, aim, now, elem, scale, 15, tag) is not { } cast)
                return;

            if (effect == TailStrikeEffect)
            {
                await SpawnTimedEffect(runner, effects, aim, TailStrikeArt, now + 1.6, scale: 1.2f);
                hits.Add(new(now + 0.85, cast.Generation, bossId, boss, cast.Attack, aim, 13, CausticGroundArt));
                pools.Add(new(aim, 10, now + 9.0, IsCaustic: true));
                await SpawnTimedEffect(runner, effects, aim, CausticGroundArt, now + 9.0, scale: 1.25f);
            }
            else if (effect == UpheavalEffect)
            {
                double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                for (int i = 1; i <= 4; i++)
                {
                    var spikeAt = WalkableNear(runner, (
                        (int)Math.Round(pos.X + Math.Cos(facing) * (i * 11)),
                        (int)Math.Round(pos.Y + Math.Sin(facing) * (i * 11))));
                    await SpawnTimedEffect(runner, effects, spikeAt, SandSpikeArt, now + 0.5 + i * 0.25 + 1.0, scale: 1.1f);
                    hits.Add(new(now + 0.45 + i * 0.25, cast.Generation, bossId, boss, cast.Attack, spikeAt, 10, null));
                }
            }
            else if (effect is TrailBurrowP1Effect or TrailBurrowP2Effect)
            {
                await SpawnTimedEffect(runner, effects, pos, BurrowSubmergeArt, now + 1.4, scale: 1.1f);
                await SpawnTimedEffect(runner, effects, aim, BurrowTrailArt, now + 1.4, scale: 1.2f);
                await TeleportBoss(runner, bossId, boss, aim, now);
                hits.Add(new(now + 0.9, cast.Generation, bossId, boss, cast.Attack, aim, 14, BurrowEmergeArt));
            }
            else
            {
                await SpawnTimedEffect(runner, effects, aim, SandSpikeArt, now + 1.5, scale: 1.4f);
                hits.Add(new(now + 0.8, cast.Generation, bossId, boss, cast.Attack, aim, 16, null));
            }
        }

        async Task SpawnQuicksandPool(Player runner, (int X, int Y) at, double radius, double until)
        {
            if (pools.Count >= 14) return;
            var spot = WalkableNear(runner, at);
            pools.Add(new(spot, radius, until, IsCaustic: false));
            await SpawnTimedEffect(runner, effects, spot, QuicksandArt, until, scale: 1.25f);
            await SpawnTimedEffect(runner, effects, spot, QuicksandRippleArt, Math.Min(until, AreaTime(runner) + 3.0), scale: 1.15f);
        }

        async Task StepDelayedHits(Player runner, double now)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var h = hits[i];
                if (now < h.At) continue;
                hits.RemoveAt(i);
                if (!Alive(h.Source)) continue;
                if (h.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, h.Spot, ao, now + 1.3, scale: 1.2f);
                await HitCircle(runner, h.SourceId, h.Source, h.Attack, h.Spot, h.Radius, () => !stopped && Alive(h.Source));
            }
        }

        async Task StepGroundsAndSandstorm(Player runner, double now)
        {
            if (now < nextDebuffTick) return;
            nextDebuffTick = now + 0.25;

            var activeBoss = p2BossId != 0 && runner.Area.Monsters.TryGetValue(p2BossId, out var b2) && Alive(b2)
                ? (Id: p2BossId, Monster: b2)
                : p1BossId != 0 && runner.Area.Monsters.TryGetValue(p1BossId, out var b1) && Alive(b1)
                    ? (Id: p1BossId, Monster: b1)
                    : default;
            if (activeBoss.Monster is null) return;

            var (minDmg, maxDmg) = Monsters.BaseDamage(activeBoss.Monster.Variety!, activeBoss.Monster.Level) ?? (25, 40);
            double baseDps = (minDmg + maxDmg) * 0.55;

            foreach (var p in ActivePlayers(runner))
            {
                var at = p.Walk.Position(now);
                bool alive = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p);
                bool inQuicksand = alive && pools.Any(h => !h.IsCaustic && Distance(at, h.At) <= h.Radius);
                bool inCaustic = alive && pools.Any(h => h.IsCaustic && Distance(at, h.At) <= h.Radius);
                bool inStorm = alive && sandstormActive && Distance(at, sandPitCenter) > sandstormSafeRadius && Distance(at, sandPitCenter) <= 135;

                if (inQuicksand)
                    await HoldPlayerDebuff(p, quicksandHolder, QuicksandBuff, activeBoss.Id, QuicksandSlowStats);
                else
                    await ReleasePlayerDebuff(p, quicksandHolder);

                if (inCaustic)
                    await HoldPlayerDebuff(p, causticHolder, CausticCloudBuff, activeBoss.Id, EmptyStats);
                else
                    await ReleasePlayerDebuff(p, causticHolder);

                if (inStorm)
                    await HoldPlayerDebuff(p, sandstormHolder, BlindedBuff, activeBoss.Id, SandstormBlindStats);
                else
                    await ReleasePlayerDebuff(p, sandstormHolder);

                if (!groundExposures.TryGetValue(p, out var exp))
                    groundExposures[p] = exp = new();

                await ArenaHazards.Ground(p, exp, now, (pt, _) =>
                {
                    var packet = DamagePacket.Empty;
                    if (pools.Any(h => !h.IsCaustic && Distance(pt, h.At) <= h.Radius))
                        packet = packet.Add(DamageType.Physical, new(baseDps * 0.55, baseDps * 0.55));
                    if (pools.Any(h => h.IsCaustic && Distance(pt, h.At) <= h.Radius))
                        packet = packet.Add(DamageType.Chaos, new(baseDps * 0.65, baseDps * 0.65));
                    if (sandstormActive && Distance(pt, sandPitCenter) > sandstormSafeRadius && Distance(pt, sandPitCenter) <= 135)
                        packet = packet.Add(DamageType.Chaos, new(baseDps * 0.75, baseDps * 0.75));
                    return packet;
                }, () => !stopped && !p2Cleaned);
            }
        }

        async Task Cleanup(Player runner)
        {
            p2Cleaned = true;
            sandstormActive = false;
            p2Burrowed = false;
            hits.Clear();
            pools.Clear();
            if (p2BossId != 0 && runner.Area.Monsters.TryGetValue(p2BossId, out var b2))
            {
                SetInvulnerable(runner, p2BossId, b2, false, CannotBeDamagedBuff);
                if (b2.WorldHidden) await SetWorldHidden(runner, p2BossId, b2, false);
            }
            await RetireMonsters(runner, canyonAdds.Concat(p2WaveAdds));
            canyonAdds.Clear();
            p2WaveAdds.Clear();
            foreach (var p in ActivePlayers(runner))
                await ReleasePlayerDebuffs(p);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Shakari P2 defeated; sandstorm, quicksand, and scorpion adds cleared");
        }
    }
}

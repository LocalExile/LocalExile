using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Acts 1–2 Boss &amp; Bandit Camp encounter polish:
/// <list type="bullet">
/// <item><b>Hillock (<c>1_1_1</c>)</b>: Phase 2 sword-pull zombie summons, plus Level 7+ Uber Hillock upgrade path
/// (<c>ZombieBossHillockSpecialEvent_</c>, fast zombie summons, and guaranteed 6-linked <c>Oni-Goroshi</c> drop).</item>
/// <item><b>Brutus (<c>1_1_7_2</c>)</b>: Chain hook pull, floor spike trap bursts (<c>spiketrap_prison.ao</c>), and arena
/// gate lock/unlock with safe cleanup on kill/wipe/leave.</item>
/// <item><b>The Weaver (<c>1_2_10</c>)</b>: Ceiling retreat at 66% / 33% HP (untargetable/hidden) with spider add waves
/// from <c>CeilingSpawners</c>, returning on wave clear or safety timer.</item>
/// <item><b>Captain Fairgraves (<c>1_1_9</c>)</b>: Pirate crew add wave summons when engaged and at 66% / 33% HP.</item>
/// <item><b>Merveil (<c>1_1_11_2</c>)</b>: Augments existing P1/P2 Merveil with <c>SeaWitchSpawn</c> /
/// <c>SeaWitchSpawnExploding</c> add waves without disturbing <c>Act1.cs</c> or <c>BossMoves</c>.</item>
/// <item><b>Act 2 Bandit Camps (<c>1_2_4</c> Kraityn, <c>1_2_9</c> Alira, <c>1_2_12</c> Oak)</b>: Locks <c>BanditDoor</c>
/// during combat and triggers camp tent/cauldron reinforcement spawners, unlocking cleanly on kill/wipe/leave.</item>
/// </list>
/// </summary>
public static class EarlyActBossPolish
{
    public const string Strand = "1_1_1";
    public const string UpperPrison = "1_1_7_2";
    public const string ShipGraveyard = "1_1_9";
    public const string CavernOfAnger = "1_1_11_2";
    public const string BrokenBridge = "1_2_4";
    public const string WesternForest = "1_2_9";
    public const string WeaversChambers = "1_2_10";
    public const string Wetlands = "1_2_12";
    public static readonly string[] Areas =
        [Strand, UpperPrison, ShipGraveyard, CavernOfAnger, BrokenBridge, WesternForest, WeaversChambers, Wetlands];

    public const string HillockNormal = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";
    public const string HillockUber = "Metadata/Monsters/ZombieBoss/ZombieBossHillockSpecialEvent_";
    public const string StrandZombie = "Metadata/Monsters/ZombieDismember/ZombieDrownedStrand1";
    public const string UberHillockAdd = "Metadata/Monsters/Zombies/ZombieMinionHillockSpecialFast";

    public const string Brutus = "Metadata/Monsters/Brute/BossBrute";
    public const string Weaver = "Metadata/Monsters/Spiders/SpiderBossWeaver";
    public const string WeaverSpiderExplode = "Metadata/Monsters/Spiders/SpiderClimbDownExplodeWeaver";
    public const string WeaverSpiderSmall = "Metadata/Monsters/Spiders/SpiderSmall";

    public const string Fairgraves = "Metadata/Monsters/Skeletons/SkeletonBossFairgraves";
    public const string PirateMinion = "Metadata/Monsters/GhostPirates/GhostPirateBlackMinion";
    public const string PirateChampion = "Metadata/Monsters/GhostPirates/GhostPirateChampion";

    public const string MerveilP1 = "Metadata/Monsters/seawitchillusion/BossMerveil";
    public const string MerveilP2 = "Metadata/Monsters/Seawitch/BossMerveil2";
    public const string MerveilSpawn = "Metadata/Monsters/Seawitch/SeaWitchSpawn";
    public const string MerveilExploding = "Metadata/Monsters/Seawitch/SeaWitchSpawnExploding";

    const string SpikeTrapArt = "Metadata/Terrain/Doodads/Traps/spiketrap_prison.ao";
    const string ArenaGateModel = "Metadata/Terrain/Doodads/Forest/BanditCamp/arenagate.ao";
    const string BanditDoorType = "Metadata/Monsters/Bandits/BanditDoor";
    const ushort HerEmbraceBuffRow = 979; // her_embrace_unique

    static readonly IReadOnlyDictionary<uint, int> UberHillockStats = new Dictionary<uint, int>
    {
        [Stat.MonsterHasOniGoroshi] = 1,
        [Stat.AttackSpeedPlusPct] = 35,
        [Stat.BaseMovementVelocityPlusPct] = 30,
        [Stat.MaximumLifePlusPct] = 150,
    };

    static readonly RuleTable<InstanceState, AreaEncounter> Encounters = new();

    public static async Task Tick(Player player)
    {
        if (!CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        string area = player.Layout.Id;
        if (area is not (Strand or UpperPrison or ShipGraveyard or CavernOfAnger
            or BrokenBridge or WesternForest or WeaversChambers or Wetlands))
            return;
        var enc = Encounters.GetValue(player.Instance, _ => new AreaEncounter(area, player.Seed));
        await enc.Gate.WaitAsync();
        try
        {
            SyncGatesForPlayer(player, enc.Gates);
            if (IsMonsterRunner(player))
                await enc.Step(player, AreaTime(player));
        }
        finally { enc.Gate.Release(); }
    }

    public static async Task OnMonsterKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Layout.Id != Strand || kill.Monster.VarietyId is not (HillockNormal or HillockUber))
            return;
        if (!Encounters.TryGetValue(player.Instance, out var enc) || !enc.IsUberHillock || enc.OniDropped)
            return;
        enc.OniDropped = true;

        // Guarantee a 6-linked Oni-Goroshi drop when Uber Hillock falls.
        if (Uniques.Create("Oni-Goroshi", AreaRng(player)) is { } oni)
        {
            if (oni.Sockets.Count < 6 || oni.Links is not [6])
            {
                var sockets = new ItemSocket[6];
                for (int i = 0; i < 6; i++)
                    sockets[i] = new ItemSocket(i < 3 ? SocketColour.Red : SocketColour.Green);
                oni = oni with { Sockets = sockets, Links = [6] };
            }
            var spot = WalkableNear(player, (kill.Monster.X, kill.Monster.Y));
            var ground = new WorldItemObject(spot.X, spot.Y, oni);
            uint gid = player.Instance.Ground.Add(ground, player, shared: true);
            await SendWorldCreate(player, gid, () => InstanceEntities.CreateObject(gid,
                InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob(justDropped: true)),
                $"0x02bb Uber Hillock drops 6L Oni-Goroshi 0x{gid:x} at ({spot.X}, {spot.Y})", wake: true, awaitBirth: false);
        }

        // If Uber Hillock replaced ZombieBossHillockNormal, ensure Act1's Hillock kill quest flag still completes.
        if (kill.Monster.VarietyId == HillockUber)
        {
            foreach (var p in ActivePlayers(player))
                if (!ClientHasQuestFlag(p, "a1q1-KilledHillock"))
                    await SetQuestFlags(p, ["a1q1-KilledHillock"]);
        }
    }

    public static Task Leaving(Player player)
    {
        if (Encounters.TryGetValue(player.Instance, out var enc))
            ReleaseGatesForPlayer(player, enc.Gates);
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Encounters) pair.Value.Stop();
        }
    }

    sealed record SpikeBurst(double At, (int X, int Y) Spot, uint SourceId, SpawnedMonster Source);

    sealed class AreaEncounter(string area, uint seed)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<ArenaGate> Gates = [];
        public bool IsUberHillock;
        public bool OniDropped;

        readonly List<TimedEffect> effects = [];
        readonly List<SpikeBurst> spikes = [];
        readonly HashSet<uint> adds = [];

        bool initialized, cleaned, stopped;
        bool hillockCheckedUber, hillockPhase2Adds;
        double nextUberAddAt;

        double nextBrutusHookAt, nextBrutusSpikesAt;
        bool brutusGateLocked;

        bool weaverHidden;
        double weaverReturnAt;
        readonly HashSet<uint> weaverWave = [];

        int fairgravesWaves;
        double nextMerveilP1AddAt;
        int merveilP2Waves;

        bool banditGatesInit, banditGatesLocked;
        int banditWaves;
        double nextBanditWaveAt;

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepSpikeBursts(runner, now);

            switch (area)
            {
                case Strand: await StepHillock(runner, now); break;
                case UpperPrison: await StepBrutus(runner, now); break;
                case WeaversChambers: await StepWeaver(runner, now); break;
                case ShipGraveyard: await StepFairgraves(runner, now); break;
                case CavernOfAnger: await StepMerveil(runner, now); break;
                case BrokenBridge or WesternForest or Wetlands: await StepBanditCamp(runner, now); break;
            }
        }

        // ---- Hillock & Uber Hillock (1_1_1) ----

        async Task StepHillock(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id is HillockNormal or HillockUber);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    await RetireMonsters(runner, adds);
                    adds.Clear();
                }
                return;
            }

            // Level 7+ Twilight Strand character upgrades Hillock to Uber Hillock before the fight starts.
            if (!hillockCheckedUber)
            {
                hillockCheckedUber = true;
                bool qualifyUber = ActivePlayers(runner).Any(p => p.Pools.Level >= 7) && boss.Life == boss.MaxLife;
                if (qualifyUber)
                {
                    IsUberHillock = true;
                    var pos = boss.Walk.Position(now);
                    if (Monsters.Find(HillockUber) is { Supported: true } uberVar && uberVar.Id == HillockUber)
                    {
                        await RetireMonsters(runner, [bossId]);
                        uint uid = await SpawnMonster(runner, uberVar, Math.Max((byte)7, boss.Level), MonsterRarity.Unique, pos.X, pos.Y,
                            owned: (id, m) =>
                            {
                                m.SetTemporaryStats("uber_hillock", UberHillockStats);
                                runner.Area.Buffs.Set(id, new(HerEmbraceBuffRow, id));
                            });
                        Console.WriteLine($"[gateway] Twilight Strand (lvl >= 7): upgraded Hillock to Uber Hillock 0x{uid:x}");
                        nextUberAddAt = now + 7;
                        return;
                    }
                    // Fallback if SpecialEvent_ variety is unavailable: empower normal Hillock directly.
                    boss.SetTemporaryStats("uber_hillock", UberHillockStats);
                    runner.Area.Buffs.Set(bossId, new(HerEmbraceBuffRow, bossId));
                    nextUberAddAt = now + 7;
                    Console.WriteLine($"[gateway] Twilight Strand (lvl >= 7): empowered Uber Hillock 0x{bossId:x}");
                }
            }

            var bossPos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, bossPos, now, maxRange: 95, awakeId: bossId);
            if (target is null) return;

            // Phase 2 sword-pull zombie summons (<= 55% HP or after Stage pull).
            if (!hillockPhase2Adds && (boss.Stage is null && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 60
                || (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 52))
            {
                hillockPhase2Adds = true;
                var rng = AreaRng(runner);
                string addType = IsUberHillock ? UberHillockAdd : StrandZombie;
                for (int i = 0; i < 3; i++)
                {
                    var spot = RingSpot(runner, bossPos, rng.NextDouble() * Math.Tau, 10 + i * 3);
                    await SpawnAdd(runner, addType, boss.Level, spot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
                }
            }

            if (IsUberHillock && now >= nextUberAddAt)
            {
                nextUberAddAt = now + 10;
                int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (living < 5)
                {
                    var rng = AreaRng(runner);
                    var spot = RingSpot(runner, bossPos, rng.NextDouble() * Math.Tau, 14);
                    await SpawnAdd(runner, UberHillockAdd, boss.Level, spot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
                }
            }
        }

        // ---- Brutus (1_1_7_2) ----

        async Task StepBrutus(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Brutus);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    spikes.Clear();
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                    brutusGateLocked = false;
                    await ClearAllEffects(runner, effects);
                }
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 95, awakeId: bossId);
            if (target is null)
            {
                if (brutusGateLocked)
                {
                    brutusGateLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                }
                return;
            }

            if (!initialized)
            {
                initialized = true;
                nextBrutusHookAt = now + 6;
                nextBrutusSpikesAt = now + 4.5;
                // Place a lockable gate behind the approaching player when Brutus engages, unlocked on kill/wipe/leave.
                var tPos = target.Walk.Position(now);
                double awayAngle = Math.Atan2(tPos.Y - pos.Y, tPos.X - pos.X);
                var gateSpot = RingSpot(runner, pos, awayAngle, Math.Min(42, Distance(pos, tPos) + 12));
                var placement = new GlyphQuest.Placement(BanditDoorType, ArenaGateModel, gateSpot.X, gateSpot.Y, (float)(awayAngle + Math.PI / 2.0), null, 0);
                if (await CreateGate(runner, BanditDoorType, placement, startLocked: true, fallbackModel: ArenaGateModel) is { } gate)
                {
                    Gates.Add(gate);
                    brutusGateLocked = true;
                }
            }
            else if (!brutusGateLocked && Gates.Count > 0)
            {
                brutusGateLocked = true;
                foreach (var g in Gates) await SetGateLocked(runner, g, true);
            }

            // Chain hook pull when target is at mid-to-long range.
            var targetPos = target.Walk.Position(now);
            double dist = Distance(pos, targetPos);
            if (now >= nextBrutusHookAt && dist is >= 14 and <= 55)
            {
                nextBrutusHookAt = now + 9.5;
                var pullSpot = RingSpot(runner, pos, Math.Atan2(targetPos.Y - pos.Y, targetPos.X - pos.X), 6);
                target.Walk.Teleport(pullSpot.X, pullSpot.Y, now);
                await SendWorldRecord(target, target.Id, InstanceProtocol.Teleport(target.Id, pullSpot.X, pullSpot.Y),
                    "[Brutus] chain hook pull");
                var hookAtk = PhysicalAttack(boss, 0.9, radius: 14);
                await ArenaHazards.Hit(target, bossId, boss, hookAtk, _ => true, () => !stopped && Alive(boss));
            }

            // Floor spike trap bursts around the player.
            if (now >= nextBrutusSpikesAt)
            {
                nextBrutusSpikesAt = now + 7.5;
                var rng = AreaRng(runner);
                for (int i = 0; i < 3; i++)
                {
                    var spot = RingSpot(runner, targetPos, rng.NextDouble() * Math.Tau, i == 0 ? 0 : 8 + i * 3);
                    await SpawnTimedEffect(runner, effects, spot, SpikeTrapArt, now + 2.2, scale: 1.0f);
                    spikes.Add(new(now + 1.15, spot, bossId, boss));
                }
            }
        }

        async Task StepSpikeBursts(Player runner, double now)
        {
            if (spikes.Count == 0) return;
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            for (int i = spikes.Count - 1; i >= 0; i--)
            {
                var s = spikes[i];
                if (now < s.At) continue;
                spikes.RemoveAt(i);
                if (!Alive(s.Source)) continue;
                var atk = PhysicalAttack(s.Source, 1.1, radius: 9);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, s.SourceId, s.Source, atk, at => Distance(at, s.Spot) <= 9, () => !stopped && Alive(s.Source));
            }
        }

        // ---- The Weaver (1_2_10) ----

        async Task StepWeaver(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Weaver);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    await RetireMonsters(runner, weaverWave);
                    weaverWave.Clear();
                }
                return;
            }

            boss.LifePhases ??= new([66, 33]);
            var pos = boss.Walk.Position(now);
            var phases = boss.LifePhases;

            if (!weaverHidden && phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                weaverHidden = true;
                weaverReturnAt = now + 14;
                SetInvulnerable(runner, bossId, boss, true);
                await SetWorldHidden(runner, bossId, boss, true);

                var spawners = PlacementsOf(WeaversChambers, seed, "Metadata/Monsters/Spiders/CeilingSpawners/NormalSwarm");
                var rng = AreaRng(runner);
                for (int i = 0; i < 5; i++)
                {
                    var spot = spawners.Count > 0
                        ? (spawners[rng.Next(spawners.Count)].X, spawners[rng.Next(spawners.Count)].Y)
                        : RingSpot(runner, pos, i * (Math.Tau / 5.0), 16);
                    string spiderType = (i & 1) == 0 ? WeaverSpiderExplode : WeaverSpiderSmall;
                    await SpawnAdd(runner, spiderType, boss.Level, spot, bossId, weaverWave,
                        ownedCurrent: () => !stopped && !cleaned);
                }
                Console.WriteLine($"[gateway] The Weaver 0x{bossId:x}: retreated to ceiling (wave {phases.Completed + 1}/2)");
                return;
            }

            if (weaverHidden)
            {
                bool spidersAlive = weaverWave.Any(id => runner.Area.Monsters.TryGetValue(id, out var s) && Alive(s));
                if (spidersAlive && now < weaverReturnAt) return;
                weaverHidden = false;
                SetInvulnerable(runner, bossId, boss, false);
                await SetWorldHidden(runner, bossId, boss, false);
                phases.Finish();
                Console.WriteLine($"[gateway] The Weaver 0x{bossId:x}: descended back into the arena");
            }
        }

        // ---- Captain Fairgraves (1_1_9) ----

        async Task StepFairgraves(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Fairgraves);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    await RetireMonsters(runner, adds);
                    adds.Clear();
                }
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 95, awakeId: bossId);
            if (target is null) return;

            int desiredWave = (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 35 ? 3
                : (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 68 ? 2 : 1;
            if (desiredWave > fairgravesWaves)
            {
                fairgravesWaves = desiredWave;
                int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                var rng = AreaRng(runner);
                for (int i = 0; i < 3 && living + i < 6; i++)
                {
                    var spot = RingSpot(runner, pos, rng.NextDouble() * Math.Tau, 14);
                    string crew = i == 0 && desiredWave >= 2 ? PirateChampion : PirateMinion;
                    await SpawnAdd(runner, crew, boss.Level, spot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
                }
                Console.WriteLine($"[gateway] Fairgraves 0x{bossId:x}: summoned pirate crew wave {fairgravesWaves}/3");
            }
        }

        // ---- Merveil (1_1_11_2) ----

        async Task StepMerveil(Player runner, double now)
        {
            var p1Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == MerveilP1);
            if (p1Entry.Value is { } p1 && Alive(p1))
            {
                var pos = p1.Walk.Position(now);
                if (NearestLivingTarget(runner, pos, now, maxRange: 105, awakeId: p1Entry.Key) is not null)
                {
                    if (nextMerveilP1AddAt == 0) nextMerveilP1AddAt = now + 6;
                    if (now >= nextMerveilP1AddAt)
                    {
                        nextMerveilP1AddAt = now + 11;
                        int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                        if (living < 4)
                        {
                            var rng = AreaRng(runner);
                            for (int i = 0; i < 2 && living + i < 4; i++)
                            {
                                var spot = RingSpot(runner, pos, rng.NextDouble() * Math.Tau, 14);
                                await SpawnAdd(runner, MerveilSpawn, p1.Level, spot, p1Entry.Key, adds,
                                    ownedCurrent: () => !stopped && !cleaned);
                            }
                        }
                    }
                }
                return;
            }

            var p2Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == MerveilP2);
            if (p2Entry.Value is not { } p2) return;
            if (!Alive(p2))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    await RetireMonsters(runner, adds);
                    adds.Clear();
                }
                return;
            }

            var p2Pos = p2.Walk.Position(now);
            if (NearestLivingTarget(runner, p2Pos, now, maxRange: 115, awakeId: p2Entry.Key) is null) return;

            int desired = (ulong)p2.Life * 100 <= (ulong)Math.Max(1u, p2.MaxLife) * 35 ? 3
                : (ulong)p2.Life * 100 <= (ulong)Math.Max(1u, p2.MaxLife) * 65 ? 2 : 1;
            if (desired > merveilP2Waves)
            {
                merveilP2Waves = desired;
                int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                var rng = AreaRng(runner);
                for (int i = 0; i < 3 && living + i < 6; i++)
                {
                    var spot = RingSpot(runner, p2Pos, rng.NextDouble() * Math.Tau, 16);
                    string kind = (i & 1) == 0 ? MerveilExploding : MerveilSpawn;
                    await SpawnAdd(runner, kind, p2.Level, spot, p2Entry.Key, adds, ownedCurrent: () => !stopped && !cleaned);
                }
            }
        }

        // ---- Act 2 Bandit Camps (1_2_4 Kraityn, 1_2_9 Alira, 1_2_12 Oak) ----

        async Task StepBanditCamp(Player runner, double now)
        {
            if (BanditChoices.In(runner) is not { } bandit) return;
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == bandit.Variety);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!banditGatesInit)
            {
                banditGatesInit = true;
                foreach (var p in PlacementsOf(area, seed, BanditDoorType))
                    if (await CreateGate(runner, BanditDoorType, p, startLocked: false, fallbackModel: ArenaGateModel) is { } g)
                        Gates.Add(g);
            }

            if (!Alive(boss))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    banditGatesLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                    await RetireMonsters(runner, adds);
                    adds.Clear();
                }
                return;
            }

            // Do not lock gates or spawn reinforcements while the player is still deciding via BanditChoices!
            if (BanditChoices.Held(runner, bandit) || boss.EntranceGrace || !boss.Hostile) return;

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 90, awakeId: bossId);
            if (target is null)
            {
                if (banditGatesLocked)
                {
                    banditGatesLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                }
                return;
            }

            if (!banditGatesLocked)
            {
                banditGatesLocked = true;
                foreach (var g in Gates) await SetGateLocked(runner, g, true);
                nextBanditWaveAt = now + 4;
            }

            if (banditWaves < 2 && now >= nextBanditWaveAt)
            {
                banditWaves++;
                nextBanditWaveAt = now + 12;
                var (spawnerTypes, mobTypes) = area switch
                {
                    BrokenBridge => (
                        new[]
                        {
                            "Metadata/Monsters/BanditLeaderDexInt/TentBanditSpawnerKraityn1",
                            "Metadata/Monsters/BanditLeaderDexInt/TentBanditSpawnerKraityn2",
                            "Metadata/Monsters/BanditLeaderDexInt/TentBanditSpawnerKraityn3",
                        },
                        new[]
                        {
                            "Metadata/Monsters/Bandits/BanditBowKraitynSpecific",
                            "Metadata/Monsters/Bandits/BanditMeleePunctureKraitynSpecific",
                        }),
                    WesternForest => (
                        new[] { "Metadata/Monsters/Zombies/CauldronSpawner" },
                        new[]
                        {
                            "Metadata/Monsters/Bandits/BanditMeleeAlira",
                            "Metadata/Monsters/Bandits/BanditBowPoisonArrow",
                        }),
                    _ => (
                        new[]
                        {
                            "Metadata/Monsters/BanditLeaderStrDex/TentBanditSpawnerOak1",
                            "Metadata/Monsters/BanditLeaderStrDex/TentBanditSpawnerOak2",
                            "Metadata/Monsters/BanditLeaderStrDex/TentBanditSpawnerOak3",
                        },
                        new[]
                        {
                            "Metadata/Monsters/Bandits/BanditBowBlastRain",
                            "Metadata/Monsters/Bandits/BanditMeleePunctureKraitynSpecific",
                        }),
                };

                var spots = spawnerTypes.SelectMany(t => PlacementsOf(area, seed, t)).Select(p => (p.X, p.Y)).ToList();
                var rng = AreaRng(runner);
                int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                for (int i = 0; i < 3 && living + i < 6; i++)
                {
                    var spot = spots.Count > 0 ? spots[rng.Next(spots.Count)] : RingSpot(runner, pos, rng.NextDouble() * Math.Tau, 18);
                    await SpawnAdd(runner, mobTypes[i % mobTypes.Length], boss.Level, spot, bossId, adds,
                        ownedCurrent: () => !stopped && !cleaned);
                }
            }
        }
    }
}

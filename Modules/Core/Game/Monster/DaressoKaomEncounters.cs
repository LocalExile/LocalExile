using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 4 — King Kaom (<c>1_4_4_3</c> Kaom's Stronghold), Daresso's Dream (<c>1_4_5_1</c> Pit enclosures), and The Grand
/// Arena (<c>1_4_5_2</c> Gladiator Trio + Daresso, King of Swords) encounter controllers.
/// </summary>
public static class DaressoKaomEncounters
{
    public const string KaomsStronghold = "1_4_4_3";
    public const string DaressosDream = "1_4_5_1";
    public const string GrandArena = "1_4_5_2";

    public const string KaomBoss = "Metadata/Monsters/Kaom/KaomBoss";
    public const string KaomSkull = "Metadata/Monsters/SummonedSkull/SummonedSkullKaom";
    public const string KaomArcher = "Metadata/Monsters/KaomWarrior/KaomArcher";
    public const string KaomFireAdd = "Metadata/Monsters/MossMonster/FireMonsterKaom";

    public const string DaressoBoss = "Metadata/Monsters/Daresso/DaressoBoss";
    public const string DaressoClone = "Metadata/Monsters/Daresso/DaressoClone";
    public const string GladiatorSword = "Metadata/Monsters/Gladiator/GladiatorSword";
    public const string GladiatorShield = "Metadata/Monsters/Gladiator/GladiatorSwordShield";
    public const string GladiatorLion = "Metadata/Monsters/Lion/LionColosseum";
    public static readonly string[] GladiatorTrio =
    [
        "Metadata/Monsters/GladiatorBosses/GladiatorBossSupport",
        "Metadata/Monsters/GladiatorBosses/GladiatorBossLeader",
        "Metadata/Monsters/GladiatorBosses/GladiatorBossElite",
    ];

    const ushort DaressoEnrageBuff = 376; // daresso_enrage
    const string EnrageStatsKey = "daresso_enrage_cycle";

    const string VolcanoChargeArt = "Metadata/Effects/Spells/monsters_effects/act4/Kaom/volcano/chargeup.ao";
    const string VolcanoImpactArt = "Metadata/Effects/Spells/monsters_effects/act4/Kaom/volcano/volcanicimpact.ao";
    const string LavaPoolArt = "Metadata/Effects/Spells/monsters_effects/act4/Kaom/volcano/loop_lavapool.ao";
    const string SwordTornadoArt = "Metadata/Effects/Spells/monsters_effects/act4/Daresso/sword_ground/swordtornado_02/swordtornado.ao";
    const string DaressoReaveArt = "Metadata/Effects/Spells/monsters_effects/act4/Daresso/reave/daressoreave.ao";

    const string PitGateType = "Metadata/Terrain/Mountain/Arena/Pit/Objects/PitGate";
    const string PitSpawnerType = "Metadata/Terrain/Mountain/Arena/Pit/Objects/PitSpawner";
    const string ColosseumDoorsType = "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoors";
    const string ColosseumDaressoDoorType = "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoorsDaresso";

    // Verified from game-files.zip .ot definitions:
    // PitGate.ot: state 1 = open, state 2 = closed (on_transition closes, on_untransition opens).
    // ColosseumDoubleDoors*.ot: state 1 = closed, state 2 = open.
    const byte PitGateLockedState = 2, PitGateUnlockedState = 1;
    const byte ColosseumDoorLockedState = 1, ColosseumDoorUnlockedState = 2;

    static readonly IReadOnlyDictionary<uint, int> DaressoEnrageStats = new Dictionary<uint, int>
    {
        [Stat.AttackSpeedPlusPct] = 45,
        [Stat.BaseMovementVelocityPlusPct] = 35,
        [Stat.PhysicalDamagePlusPct] = 25,
    };

    static readonly RuleTable<InstanceState, KaomFight> KaomFights = new();
    static readonly RuleTable<InstanceState, DreamPits> Pits = new();
    static readonly RuleTable<InstanceState, GrandArenaFight> ArenaFights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> GroundExposures = new();

    public static async Task Tick(Player player)
    {
        if (!CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        string area = player.Layout.Id;
        if (area == KaomsStronghold) await TickKaom(player);
        else if (area == DaressosDream) await TickPits(player);
        else if (area == GrandArena) await TickGrandArena(player);
    }

    public static Task Leaving(Player player)
    {
        if (GroundExposures.TryGetValue(player, out var exp))
            exp.Last = double.NaN;
        if (player.Layout.Id == DaressosDream && Pits.TryGetValue(player.Instance, out var pit))
            ReleaseGatesForPlayer(player, pit.AllGates);
        if (player.Layout.Id == GrandArena && ArenaFights.TryGetValue(player.Instance, out var arena))
            ReleaseGatesForPlayer(player, arena.AllGates);
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var p in KaomFights) p.Value.Stop();
            foreach (var p in Pits) p.Value.Stop();
            foreach (var p in ArenaFights) p.Value.StopAndClear();
            foreach (var p in GroundExposures) p.Value.Last = double.NaN;
            ReleaseAllGatesOnUnload();
        }
    }

    static void ResetGroundExposures(Player runner)
    {
        foreach (var p in ActivePlayers(runner))
            if (GroundExposures.TryGetValue(p, out var exp))
                exp.Last = double.NaN;
    }

    // ---- King Kaom (1_4_4_3) ----

    static async Task TickKaom(Player player)
    {
        if (!KaomFights.TryGetValue(player.Instance, out var fight))
        {
            bool hasKaom = player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id == KaomBoss);
            if (!hasKaom) return;
            fight = KaomFights.GetValue(player.Instance, _ => new KaomFight(player.Seed));
        }
        await fight.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    sealed record LavaZone((int X, int Y) At, double Ends, double Radius);
    sealed record EruptionImpact(double At, (int X, int Y) Spot, double Radius);

    sealed class KaomFight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<LavaZone> lava = [];
        readonly List<EruptionImpact> impacts = [];
        readonly HashSet<uint> adds = [];
        static readonly int[] WaveThresholds = [75, 50, 25];

        readonly List<(int X, int Y)> volcanoes;
        readonly List<(int X, int Y)> archerSpawners;
        readonly List<(int X, int Y)> skullSpawners;

        bool initialized, cleaned, stopped;
        int wavesTriggered;
        double nextEruptionAt;

        public KaomFight(uint seed)
        {
            var placements = LoadAreaPlacements(KaomsStronghold, seed);
            volcanoes = [.. placements.Of("Metadata/Monsters/Kaom/Volcano/kaom_volcano").Select(p => (p.X, p.Y))];
            archerSpawners = [.. placements.Of("Metadata/Terrain/Mountain/Plateau/Objects/KaomBossArcherSpawner").Select(p => (p.X, p.Y))];
            skullSpawners = [.. placements.Of("Metadata/Terrain/Mountain/Plateau/Objects/KaomBossSkullSpawnerMedium").Select(p => (p.X, p.Y))];
        }

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            lava.RemoveAll(z => now >= z.Ends);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == KaomBoss);
            if (entry.Value is not { } boss || !Alive(boss))
            {
                if (initialized && !cleaned) await Cleanup(runner);
                return;
            }
            uint bossId = entry.Key;

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 125, awakeId: bossId);
            if (target is null)
            {
                ResetGroundExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                nextEruptionAt = now + 6;
                if (volcanoes.Count == 0)
                    for (int i = 0; i < 3; i++) volcanoes.Add(RingSpot(runner, pos, i * (Math.Tau / 3.0), 26));
                Console.WriteLine($"[gateway] Kaom controller active for 0x{bossId:x} ({volcanoes.Count} volcanoes)");
            }

            while (wavesTriggered < WaveThresholds.Length
                && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * (ulong)WaveThresholds[wavesTriggered])
            {
                wavesTriggered++;
                await SpawnKaomWave(runner, bossId, boss, pos);
            }

            if (now >= nextEruptionAt)
            {
                nextEruptionAt = now + 10;
                var rng = AreaRng(runner);
                var vSpot = volcanoes[rng.Next(volcanoes.Count)];
                await SpawnTimedEffect(runner, effects, vSpot, VolcanoChargeArt, now + 2.0, scale: 1.2f);
                var aim = target.Walk.Position(now);
                for (int i = 0; i < 3; i++)
                {
                    var drop = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 4 + i * 7);
                    impacts.Add(new(now + 1.5 + i * 0.35, drop, 11));
                }
            }

            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            for (int i = impacts.Count - 1; i >= 0; i--)
            {
                var imp = impacts[i];
                if (now < imp.At) continue;
                impacts.RemoveAt(i);
                await SpawnTimedEffect(runner, effects, imp.Spot, VolcanoImpactArt, now + 1.1, scale: 1.1f);
                await SpawnTimedEffect(runner, effects, imp.Spot, LavaPoolArt, now + 7.0, scale: 1.0f);
                lava.Add(new(imp.Spot, now + 7.0, 9.5));
                var atk = ElementalSpellAttack(boss, DamageType.Fire, 1.35, imp.Radius);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, imp.Spot) <= imp.Radius, () => !stopped && Alive(boss));
            }

            if (lava.Count > 0)
            {
                var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (25, 35);
                double burnDps = (min + max) * 0.45;
                foreach (var p in players)
                {
                    var exp = GroundExposures.GetValue(p, _ => new());
                    await ArenaHazards.Ground(p, exp, now, (at, _) =>
                        lava.Any(z => Distance(at, z.At) <= z.Radius)
                            ? DamagePacket.Of(DamageType.Fire, burnDps, burnDps)
                            : DamagePacket.Empty,
                        () => !stopped && Alive(boss));
                }
            }
            else
            {
                ResetGroundExposures(runner);
            }
        }

        async Task SpawnKaomWave(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) center)
        {
            int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            var rng = AreaRng(runner);
            for (int i = 0; i < 3 && living < 8; i++, living++)
            {
                var spot = skullSpawners.Count > 0
                    ? skullSpawners[rng.Next(skullSpawners.Count)]
                    : RingSpot(runner, center, rng.NextDouble() * Math.Tau, 24);
                await SpawnAdd(runner, KaomSkull, boss.Level, spot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
            }
            if (living < 8)
            {
                var aSpot = archerSpawners.Count > 0
                    ? archerSpawners[rng.Next(archerSpawners.Count)]
                    : RingSpot(runner, center, rng.NextDouble() * Math.Tau, 28);
                await SpawnAdd(runner, KaomArcher, boss.Level, aSpot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
                living++;
            }
            if (living < 8)
            {
                var fSpot = RingSpot(runner, center, rng.NextDouble() * Math.Tau, 20);
                await SpawnAdd(runner, KaomFireAdd, boss.Level, fSpot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
            }
            Console.WriteLine($"[gateway] Kaom wave {wavesTriggered}/3 spawned");
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            lava.Clear();
            impacts.Clear();
            ResetGroundExposures(runner);
            await RetireMonsters(runner, adds);
            adds.Clear();
            await ClearAllEffects(runner, effects);
        }
    }

    // ---- Daresso's Dream Pit Enclosures (1_4_5_1) ----

    static async Task TickPits(Player player)
    {
        var pits = Pits.GetValue(player.Instance, _ => new DreamPits(player.Seed));
        await pits.Gate.WaitAsync();
        try
        {
            SyncGatesForPlayer(player, pits.AllGates);
            if (IsMonsterRunner(player))
                await pits.Step(player, AreaTime(player));
        }
        finally { pits.Gate.Release(); }
    }

    sealed class PitEnclosure((int X, int Y) center, List<(int X, int Y)> spawners, List<GlyphQuest.Placement> gatePlacements)
    {
        public readonly (int X, int Y) Center = center;
        public readonly List<(int X, int Y)> Spawners = spawners;
        public readonly List<GlyphQuest.Placement> GatePlacements = gatePlacements;
        public readonly List<ArenaGate> Gates = [];
        public readonly HashSet<uint> WaveMonsters = [];
        public int Wave; // 0 = idle, 1..2 = active waves, 3 = cleared
    }

    sealed class DreamPits
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<PitEnclosure> enclosures = [];
        bool initialized, stopped;

        public IEnumerable<ArenaGate> AllGates => enclosures.SelectMany(e => e.Gates);

        public DreamPits(uint seed)
        {
            var placements = LoadAreaPlacements(DaressosDream, seed);
            var spawners = placements.Of(PitSpawnerType);
            var gates = placements.Of(PitGateType);
            if (spawners.Count == 0) return;

            var remaining = new List<GlyphQuest.Placement>(spawners);
            while (remaining.Count > 0)
            {
                var anchor = remaining[0];
                remaining.RemoveAt(0);
                var cluster = new List<(int X, int Y)> { (anchor.X, anchor.Y) };
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (Distance((remaining[i].X, remaining[i].Y), (anchor.X, anchor.Y)) <= 55)
                    {
                        cluster.Add((remaining[i].X, remaining[i].Y));
                        remaining.RemoveAt(i);
                    }
                }
                var center = ((int)Math.Round(cluster.Average(p => p.X)), (int)Math.Round(cluster.Average(p => p.Y)));
                var nearbyGates = gates.Where(g => Distance((g.X, g.Y), center) <= 55).ToList();
                enclosures.Add(new(center, cluster, nearbyGates));
            }
        }

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            if (!initialized)
            {
                initialized = true;
                foreach (var pit in enclosures)
                    foreach (var gp in pit.GatePlacements)
                        if (await CreateGate(runner, PitGateType, gp, startLocked: false, PitGateLockedState, PitGateUnlockedState) is { } g)
                            pit.Gates.Add(g);
            }
            if (enclosures.Count == 0) return;

            var livingPlayers = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            byte level = (byte)Math.Clamp(Packs.For(DaressosDream)?.AreaLevel ?? 37, 1, 100);

            foreach (var pit in enclosures)
            {
                if (pit.Wave == 3) continue;

                bool playerInside = livingPlayers.Any(p => Distance(p.Walk.Position(now), pit.Center) <= 28);
                if (pit.Wave == 0)
                {
                    if (!playerInside) continue;
                    pit.Wave = 1;
                    foreach (var g in pit.Gates) await SetGateLocked(runner, g, true);
                    await SpawnPitWave(runner, pit, level, wave: 1);
                    Console.WriteLine($"[gateway] Daresso's Dream pit at {pit.Center}: gates locked, wave 1 spawned");
                    continue;
                }

                if (!livingPlayers.Any(p => Distance(p.Walk.Position(now), pit.Center) <= 65))
                {
                    foreach (var g in pit.Gates) await SetGateLocked(runner, g, false);
                    continue;
                }

                bool anyAlive = pit.WaveMonsters.Any(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (anyAlive) continue;

                pit.WaveMonsters.Clear();
                if (pit.Wave == 1)
                {
                    pit.Wave = 2;
                    await SpawnPitWave(runner, pit, level, wave: 2);
                }
                else
                {
                    pit.Wave = 3;
                    foreach (var g in pit.Gates) await SetGateLocked(runner, g, false);
                    Console.WriteLine($"[gateway] Daresso's Dream pit at {pit.Center}: cleared, gates unlocked");
                }
            }
        }

        async Task SpawnPitWave(Player runner, PitEnclosure pit, byte level, int wave)
        {
            var rng = AreaRng(runner);
            int count = wave == 1 ? 4 : 5;
            for (int i = 0; i < count; i++)
            {
                var spot = pit.Spawners[rng.Next(pit.Spawners.Count)];
                string variety = (i % 3) switch { 0 => GladiatorSword, 1 => GladiatorShield, _ => GladiatorLion };
                await SpawnAdd(runner, variety, level, spot, 0, pit.WaveMonsters,
                    ownedCurrent: () => !stopped && pit.Wave < 3);
            }
        }
    }

    // ---- The Grand Arena: Gladiator Trio + Daresso (1_4_5_2) ----

    static async Task TickGrandArena(Player player)
    {
        var fight = ArenaFights.GetValue(player.Instance, _ => new GrandArenaFight(player.Area, player.Seed));
        await fight.Gate.WaitAsync();
        try
        {
            SyncGatesForPlayer(player, fight.AllGates);
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    sealed record SwordVortex((int X, int Y) At, double Ends, double Radius);

    sealed class GrandArenaFight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<SwordVortex> vortexes = [];
        readonly List<GlyphQuest.Placement> trioDoorPlacements;
        readonly List<GlyphQuest.Placement> daressoDoorPlacements;
        readonly List<ArenaGate> trioDoors = [];
        readonly List<ArenaGate> daressoDoors = [];
        readonly HashSet<uint> clones = [];
        static readonly int[] EnrageThresholds = [75, 50, 25];

        uint daressoBossId;
        bool doorsInitialized, trioLocked, daressoInitialized, daressoCleaned, stopped;
        int enrageCount;
        double enrageEndsAt, nextVortexAt, nextReaveAt;

        public IEnumerable<ArenaGate> AllGates => trioDoors.Concat(daressoDoors);

        public GrandArenaFight(GameArea area, uint seed)
        {
            this.area = area;
            var placements = LoadAreaPlacements(GrandArena, seed);
            trioDoorPlacements = placements.Of(ColosseumDoorsType);
            daressoDoorPlacements = placements.Of(ColosseumDaressoDoorType);
        }

        public void StopAndClear()
        {
            stopped = true;
            if (daressoBossId != 0 && area.Monsters.TryGetValue(daressoBossId, out var b))
            {
                b.RemoveTemporaryStats(EnrageStatsKey);
                b.RebuildStats();
                area.Buffs.Clear(daressoBossId, DaressoEnrageBuff, daressoBossId);
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            vortexes.RemoveAll(v => now >= v.Ends);

            if (!doorsInitialized)
            {
                doorsInitialized = true;
                foreach (var p in trioDoorPlacements)
                    if (await CreateGate(runner, ColosseumDoorsType, p, startLocked: false, ColosseumDoorLockedState, ColosseumDoorUnlockedState) is { } g)
                        trioDoors.Add(g);
                foreach (var p in daressoDoorPlacements)
                    if (await CreateGate(runner, ColosseumDaressoDoorType, p, startLocked: false, ColosseumDoorLockedState, ColosseumDoorUnlockedState) is { } g)
                        daressoDoors.Add(g);
            }

            await StepGladiatorTrio(runner, now);

            var dEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == DaressoBoss);
            if (dEntry.Value is { } daresso)
            {
                daressoBossId = dEntry.Key;
                if (Alive(daresso))
                    await StepDaresso(runner, dEntry.Key, daresso, now);
                else if (!daressoCleaned)
                    await CleanupDaresso(runner, dEntry.Key, daresso);
            }
        }

        async Task StepGladiatorTrio(Player runner, double now)
        {
            var livingTrio = runner.Area.Monsters.Where(p => p.Value.Variety is { } v && GladiatorTrio.Contains(v.Id) && Alive(p.Value)).ToArray();
            if (livingTrio.Length == 0)
            {
                if (trioLocked)
                {
                    trioLocked = false;
                    foreach (var d in trioDoors) await SetGateLocked(runner, d, false);
                }
                return;
            }
            bool engaged = false;
            foreach (var gladiator in livingTrio)
            {
                if (NearestLivingTarget(runner, gladiator.Value.Walk.Position(now), now, maxRange: 65, awakeId: gladiator.Key) is not null)
                {
                    engaged = true;
                    break;
                }
            }
            if (engaged != trioLocked)
            {
                trioLocked = engaged;
                foreach (var d in trioDoors) await SetGateLocked(runner, d, engaged);
            }
        }

        async Task StepDaresso(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 120, awakeId: bossId);
            if (target is null)
            {
                foreach (var d in daressoDoors) await SetGateLocked(runner, d, false);
                ResetGroundExposures(runner);
                return;
            }

            if (!daressoInitialized)
            {
                daressoInitialized = true;
                nextVortexAt = now + 5;
                nextReaveAt = now + 9;
                Console.WriteLine($"[gateway] Daresso controller active for 0x{bossId:x}");
            }
            foreach (var d in daressoDoors) await SetGateLocked(runner, d, true);

            if (enrageEndsAt > 0 && now >= enrageEndsAt)
            {
                enrageEndsAt = 0;
                boss.RemoveTemporaryStats(EnrageStatsKey);
                boss.RebuildStats();
                runner.Area.Buffs.Clear(bossId, DaressoEnrageBuff, bossId);
            }

            while (enrageCount < EnrageThresholds.Length
                && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * (ulong)EnrageThresholds[enrageCount])
            {
                enrageCount++;
                enrageEndsAt = now + 8.0;
                boss.SetTemporaryStats(EnrageStatsKey, DaressoEnrageStats);
                boss.RebuildStats();
                runner.Area.Buffs.Set(bossId, new(DaressoEnrageBuff, bossId, U68: Buff.Seconds(8)));
                var rng = AreaRng(runner);
                int livingClones = clones.Count(id => runner.Area.Monsters.TryGetValue(id, out var c) && Alive(c));
                for (int i = 0; i < 2 && livingClones + i < 4; i++)
                {
                    var cSpot = RingSpot(runner, pos, rng.NextDouble() * Math.Tau, 14);
                    await SpawnAdd(runner, DaressoClone, boss.Level, cSpot, bossId, clones,
                        ownedCurrent: () => !stopped && !daressoCleaned && Alive(boss));
                }
                Console.WriteLine($"[gateway] Daresso 0x{bossId:x}: enrage cycle {enrageCount}/3 + sword clones");
            }

            if (now >= nextVortexAt)
            {
                nextVortexAt = now + 7.5;
                var aim = target.Walk.Position(now);
                var rng = AreaRng(runner);
                for (int i = 0; i < 2; i++)
                {
                    var spot = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 6 + i * 8);
                    await SpawnTimedEffect(runner, effects, spot, SwordTornadoArt, now + 8.0, scale: 1.0f);
                    vortexes.Add(new(spot, now + 8.0, 9.0));
                }
            }

            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();

            if (now >= nextReaveAt)
            {
                nextReaveAt = now + (enrageEndsAt > now ? 5.5 : 8.5);
                var aim = target.Walk.Position(now);
                await SpawnTimedEffect(runner, effects, aim, DaressoReaveArt, now + 1.1, scale: 1.15f);
                var atk = PhysicalAttack(boss, enrageEndsAt > now ? 1.45 : 1.15, radius: 14);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, aim) <= 14, () => !stopped && Alive(boss));
            }

            if (vortexes.Count > 0)
            {
                var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (25, 35);
                double bladeDps = (min + max) * 0.5;
                foreach (var p in players)
                {
                    var exp = GroundExposures.GetValue(p, _ => new());
                    await ArenaHazards.Ground(p, exp, now, (at, _) =>
                        vortexes.Any(v => Distance(at, v.At) <= v.Radius)
                            ? DamagePacket.Of(DamageType.Physical, bladeDps, bladeDps)
                            : DamagePacket.Empty,
                        () => !stopped && Alive(boss));
                }
            }
            else
            {
                ResetGroundExposures(runner);
            }
        }

        async Task CleanupDaresso(Player runner, uint bossId, SpawnedMonster boss)
        {
            daressoCleaned = true;
            boss.RemoveTemporaryStats(EnrageStatsKey);
            boss.RebuildStats();
            runner.Area.Buffs.Clear(bossId, DaressoEnrageBuff, bossId);
            foreach (var d in daressoDoors) await SetGateLocked(runner, d, false);
            await RetireMonsters(runner, clones);
            clones.Clear();
            vortexes.Clear();
            ResetGroundExposures(runner);
            await ClearAllEffects(runner, effects);
        }
    }
}

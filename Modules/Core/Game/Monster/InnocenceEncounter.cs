using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 5 — The Chamber of Innocence (<c>1_5_5</c>) Avarius &amp; Innocence encounter controller:
/// <list type="bullet">
/// <item><b>Phase 1 (<c>AvariusCasticus</c>)</b>: Ensures <c>NoRewards</c> on Avarius (whose death spawns
/// <c>AvariusCasticusDivine</c> via <c>Act5.cs</c>'s <c>On.SecondForm</c>), places the 4 dormant
/// <c>AvariusCasticusStatue</c> Guardians of Eternity at their <c>boss_02.arm</c> coordinates, and spawns a 50% HP
/// Templar reinforcement wave.</item>
/// <item><b>Phase 2 (<c>AvariusCasticusDivine</c>, Innocence)</b>: Four statue invulnerability phases at 80% / 60% / 40% /
/// 20% HP where Innocence hovers invulnerable at <c>ArenaMiddle</c> and animates the 4 <c>AvariusCasticusStatue</c>
/// statues one by one; destroying the animated statue immediately drops Innocence's invulnerability. Also runs his
/// channelled sweeping <c>AvariusDivineBeam</c> and "I am the Torch!" rotating golden orb bullet-hell nova.</item>
/// </list>
/// </summary>
public static class InnocenceEncounter
{
    public const string Area = "1_5_5";
    public const string Avarius = "Metadata/Monsters/AvariusCasticus/AvariusCasticus";
    public const string Innocence = "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine";
    public const string Statue = "Metadata/Monsters/AvariusCasticus/AvariusCasticusStatue";
    public const string PrayerGuard = "Metadata/Monsters/HolyFireElemental/HolyFireElementalAvarius1";
    public const string LightborneApostle = "Metadata/Monsters/AvariusMinion/EnlightenedDuelist_";

    const ushort CannotBeDamagedBuff = 350;  // cannot_be_damaged
    const ushort DivineBeamDebuff = 840;     // avarius_divine_beam_debuff
    const ushort StatueBuff = 841;           // avarius_statue_buff

    const string ActivateStatuesArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/activate_statues/activate_statues.ao";
    const string BeamChargeArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/beam_charge/rig.ao";
    const string DashEndSlamArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/dash_endslam/dash_endslam.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id is Avarius or Innocence);
            if (!present) return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player.Seed));
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
        if (!player.Closed)
            player.Area.Buffs.Clear(player.Id, DivineBeamDebuff, player.Id);
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
        }
    }

    sealed class SweepingBeam(uint sourceId, SpawnedMonster source, (int X, int Y) origin, double startAngle, double sweepRad, double started, double ends)
    {
        public readonly uint SourceId = sourceId;
        public readonly SpawnedMonster Source = source;
        public readonly (int X, int Y) Origin = origin;
        public readonly double StartAngle = startAngle;
        public readonly double SweepRad = sweepRad;
        public readonly double Started = started;
        public readonly double Ends = ends;
        public double NextPulse = started;
    }

    sealed class BulletHellNova(uint sourceId, SpawnedMonster source, (int X, int Y) origin, double started, double ends)
    {
        public readonly uint SourceId = sourceId;
        public readonly SpawnedMonster Source = source;
        public readonly (int X, int Y) Origin = origin;
        public readonly double Started = started;
        public readonly double Ends = ends;
        public int WaveIndex;
        public double NextWaveAt = started;
    }

    sealed class Fight(uint seed)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly uint[] statueIds = new uint[4];
        readonly HashSet<uint> adds = [];

        (int X, int Y) arenaMiddle;
        bool statuesPlaced, p1WaveSent, p2Initialized, cleaned, stopped;
        uint p2BossId;
        int activeStatueIndex = -1;
        double nextBeamAt, nextBulletHellAt;
        SweepingBeam? activeBeam;
        BulletHellNova? activeNova;

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);

            var p1Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Avarius);
            if (p1Entry.Value is { } p1 && Alive(p1))
                await StepAvarius(runner, p1Entry.Key, p1, now);

            var p2Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Innocence);
            if (p2Entry.Value is { } p2)
            {
                if (Alive(p2))
                    await StepInnocence(runner, p2Entry.Key, p2, now);
                else if (!cleaned)
                    await Cleanup(runner);
            }
        }

        async Task StepAvarius(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            boss.NoRewards = true;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 120, awakeId: bossId);
            if (target is null) return;

            if (!statuesPlaced)
                await PlaceDormantStatues(runner, bossId, boss.Level, pos);

            if (!p1WaveSent && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 55)
            {
                p1WaveSent = true;
                await SpawnTemplarWave(runner, bossId, boss.Level, arenaMiddle, count: 3);
            }
        }

        async Task StepInnocence(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            p2BossId = bossId;
            boss.LifePhases ??= new([80, 60, 40, 20]);
            var pos = boss.Walk.Position(now);

            if (!statuesPlaced)
                await PlaceDormantStatues(runner, bossId, boss.Level, pos);

            if (!p2Initialized)
            {
                p2Initialized = true;
                nextBeamAt = now + 7;
                nextBulletHellAt = now + 13;
                Console.WriteLine($"[gateway] Innocence controller active for 0x{bossId:x} with 4 statue phases");
            }

            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: bossId);
            if (target is null) return;

            var phases = boss.LifePhases;
            if (!phases.Active && phases.Completed < 4 && phases.Begin(boss.Life, boss.MaxLife))
            {
                activeStatueIndex = phases.Completed;
                activeBeam = null;
                activeNova = null;
                SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                boss.Walk.Teleport(arenaMiddle.X, arenaMiddle.Y, now);
                await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, arenaMiddle.X, arenaMiddle.Y), null);
                await SpawnTimedEffect(runner, effects, arenaMiddle, ActivateStatuesArt, now + 2.2, scale: 1.2f);
                await AnimateStatue(runner, bossId, boss.Level, activeStatueIndex);
            }

            if (phases.Active && activeStatueIndex is >= 0 and < 4)
            {
                uint sid = statueIds[activeStatueIndex];
                bool statueDead = sid == 0 || !runner.Area.Monsters.TryGetValue(sid, out var sm) || !Alive(sm);
                if (statueDead)
                {
                    activeStatueIndex = -1;
                    SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
                    phases.Finish();
                    Console.WriteLine($"[gateway] Innocence: statue {phases.Completed}/4 defeated; Innocence vulnerable");
                }
                return;
            }

            // Active combat: Sweeping Divine Beam + Bullet-Hell Golden Orb Nova ("I am the Torch!").
            await StepBeam(runner, now);
            await StepBulletHell(runner, now);

            if (activeBeam is null && activeNova is null)
            {
                if (now >= nextBeamAt)
                {
                    nextBeamAt = now + 14;
                    var aim = target.Walk.Position(now);
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X) - Math.PI / 4.0;
                    await SpawnTimedEffect(runner, effects, pos, BeamChargeArt, now + 3.6, scale: 1.1f);
                    activeBeam = new(bossId, boss, pos, angle, Math.PI / 2.0, now + 1.1, now + 3.6);
                }
                else if (now >= nextBulletHellAt)
                {
                    nextBulletHellAt = now + 18;
                    await SpawnTimedEffect(runner, effects, pos, DashEndSlamArt, now + 3.2, scale: 1.3f);
                    activeNova = new(bossId, boss, pos, now + 0.8, now + 3.8);
                }
            }
        }

        async Task PlaceDormantStatues(Player runner, uint ownerId, byte level, (int X, int Y) fallbackCenter)
        {
            statuesPlaced = true;
            var mid = PlacementsOf(Area, seed, "Metadata/Monsters/AvariusCasticus/ArenaMiddle").FirstOrDefault();
            arenaMiddle = mid is not null ? (mid.X, mid.Y) : fallbackCenter;

            var spawners = PlacementsOf(Area, seed, "Metadata/Monsters/AvariusCasticus/AvariusStatueSpawner");
            if (spawners.Count < 4)
                spawners = PlacementsOf(Area, seed, Statue);

            for (int i = 0; i < 4; i++)
            {
                var spot = i < spawners.Count
                    ? (spawners[i].X, spawners[i].Y)
                    : RingSpot(runner, arenaMiddle, (Math.PI / 4.0) + i * (Math.PI / 2.0), 34);
                uint sid = await SpawnAdd(runner, Statue, level, spot, ownerId,
                    entranceGrace: true, rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !cleaned);
                statueIds[i] = sid;
                if (sid != 0 && runner.Area.Monsters.TryGetValue(sid, out var sm))
                {
                    SetInvulnerable(runner, sid, sm, true, CannotBeDamagedBuff);
                    await SetMonsterTransition(runner, sid, sm, 1);
                }
            }
        }

        async Task AnimateStatue(Player runner, uint bossId, byte level, int index)
        {
            uint sid = statueIds[index];
            if (sid != 0 && runner.Area.Monsters.TryGetValue(sid, out var sm) && Alive(sm))
            {
                sm.EntranceGrace = false;
                sm.Aggro = true;
                SetInvulnerable(runner, sid, sm, false, CannotBeDamagedBuff);
                runner.Area.Buffs.Set(sid, new(StatueBuff, sid));
                await SetMonsterTransition(runner, sid, sm, 2, $"[Innocence] statue {index + 1}/4 animates");
                Console.WriteLine($"[gateway] Innocence: animated Guardian of Eternity {index + 1}/4 (0x{sid:x})");
            }
            await SpawnTemplarWave(runner, bossId, level, arenaMiddle, count: 2);
        }

        async Task SpawnTemplarWave(Player runner, uint bossId, byte level, (int X, int Y) center, int count)
        {
            int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            var rng = AreaRng(runner);
            for (int i = 0; i < count && living + i < 6; i++)
            {
                var spot = RingSpot(runner, center, rng.NextDouble() * Math.Tau, 24);
                string kind = (i & 1) == 0 ? PrayerGuard : LightborneApostle;
                await SpawnAdd(runner, kind, level, spot, bossId, adds,
                    ownedCurrent: () => !stopped && !cleaned);
            }
        }

        async Task StepBeam(Player runner, double now)
        {
            if (activeBeam is not { } beam) return;
            if (now >= beam.Ends || !Alive(beam.Source))
            {
                activeBeam = null;
                return;
            }
            if (now < beam.NextPulse) return;
            beam.NextPulse = now + 0.35;

            double t = Math.Clamp((now - beam.Started) / Math.Max(0.1, beam.Ends - beam.Started), 0, 1);
            double angle = beam.StartAngle + beam.SweepRad * t;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var atk = ElementalSpellAttack(beam.Source, DamageType.Fire, 0.85, radius: 70);
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();

            foreach (var p in players)
            {
                bool HitLine((int X, int Y) at)
                {
                    double dx = at.X - beam.Origin.X, dy = at.Y - beam.Origin.Y;
                    double along = dx * cos + dy * sin;
                    if (along < 4 || along > 70) return false;
                    double perp = Math.Abs(-dx * sin + dy * cos);
                    return perp <= 6.5;
                }
                if (HitLine(p.Walk.Position(now)))
                    p.Area.Buffs.Set(p.Id, new(DivineBeamDebuff, beam.SourceId, U68: Buff.Seconds(2)));
                await ArenaHazards.Hit(p, beam.SourceId, beam.Source, atk, HitLine, () => !stopped && Alive(beam.Source));
            }
        }

        async Task StepBulletHell(Player runner, double now)
        {
            if (activeNova is not { } nova) return;
            if (now >= nova.Ends || !Alive(nova.Source))
            {
                activeNova = null;
                return;
            }
            if (now < nova.NextWaveAt) return;
            nova.NextWaveAt = now + 0.9;
            int wave = nova.WaveIndex++;

            // 10 golden orbs per ring with staggered angular gaps so players can weave between orbs.
            double ringRadius = 14 + wave * 16;
            double angleOffset = wave * (Math.PI / 10.0);
            var orbPositions = new (int X, int Y)[10];
            for (int i = 0; i < 10; i++)
            {
                double a = angleOffset + i * (Math.Tau / 10.0);
                orbPositions[i] = (
                    (int)Math.Round(nova.Origin.X + Math.Cos(a) * ringRadius),
                    (int)Math.Round(nova.Origin.Y + Math.Sin(a) * ringRadius));
            }
            var atk = ElementalSpellAttack(nova.Source, DamageType.Lightning, 1.1, radius: 8);
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            foreach (var p in players)
                await ArenaHazards.Hit(p, nova.SourceId, nova.Source, atk,
                    at => orbPositions.Any(o => Distance(at, o) <= 6.5),
                    () => !stopped && Alive(nova.Source));
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            activeBeam = null;
            activeNova = null;
            if (p2BossId != 0 && runner.Area.Monsters.TryGetValue(p2BossId, out var b))
                SetInvulnerable(runner, p2BossId, b, false, CannotBeDamagedBuff);
            await RetireMonsters(runner, statueIds.Where(id => id != 0));
            Array.Clear(statueIds);
            await RetireMonsters(runner, adds);
            adds.Clear();
            foreach (var p in ActivePlayers(runner))
                p.Area.Buffs.Clear(p.Id, DivineBeamDebuff, p.Id);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Innocence defeated; statues, adds, and hazards cleared");
        }
    }
}

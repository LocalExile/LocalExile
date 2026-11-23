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
/// <c>AvariusCasticusStatue</c> props at their cached <c>boss_02.arm</c> coordinates, and spawns a 55% HP
/// Templar reinforcement wave.</item>
/// <item><b>Phase 2 (<c>AvariusCasticusDivine</c>, Innocence)</b>: Four statue invulnerability phases at 80% / 60% / 40% /
/// 20% HP where Innocence hovers invulnerable at <c>ArenaMiddle</c>. Because <c>AvariusCasticusStatue.ot</c> sets
/// <c>Targetable { targetable = false }</c> and buff 841 is the dormant petrification buff, animating statue <c>i</c>
/// retires the dormant prop and spawns a targetable Unique animated Guardian (<c>HolyFireElementalAvarius1</c>,
/// <c>NoRewards</c>) at that statue's exact coordinates; killing (or losing) the animated guardian immediately drops
/// Innocence's invulnerability. Survives portal re-entry by re-syncing <c>LifePhases</c> and uncompleted statues.
/// Also runs his channelled sweeping <c>AvariusDivineBeam</c> and "I am the Torch!" golden orb bullet-hell nova.</item>
/// </list>
/// </summary>
public static class InnocenceEncounter
{
    public const string Area = "1_5_5";
    public const string Avarius = "Metadata/Monsters/AvariusCasticus/AvariusCasticus";
    public const string Innocence = "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine";
    public const string DormantStatue = "Metadata/Monsters/AvariusCasticus/AvariusCasticusStatue";
    public const string AnimatedGuardian = "Metadata/Monsters/HolyFireElemental/HolyFireElementalAvarius1";
    public const string LightborneApostle = "Metadata/Monsters/AvariusMinion/EnlightenedDuelist_";

    const ushort CannotBeDamagedBuff = 350;  // cannot_be_damaged
    const ushort DivineBeamDebuff = 840;     // avarius_divine_beam_debuff

    const string ActivateStatuesArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/activate_statues/activate_statues.ao";
    const string BeamChargeArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/beam_charge/rig.ao";
    const string DashEndSlamArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/dash_endslam/dash_endslam.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, BeamDebuffMarker> BeamDebuffedPlayers = new();

    sealed class BeamDebuffMarker { public bool Active; }

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id is Avarius or Innocence);
            if (!present) return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player.Area, player.Seed));
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
        ClearBeamDebuff(player);
        return Task.CompletedTask;
    }

    static void ClearBeamDebuff(Player player)
    {
        if (!BeamDebuffedPlayers.TryGetValue(player, out var marker) || !marker.Active) return;
        marker.Active = false;
        if (!player.Closed)
            player.Area.Buffs.Clear(player.Id, DivineBeamDebuff, player.Id);
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in BeamDebuffedPlayers) ClearBeamDebuff(pair.Key);
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

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly uint[] dormantStatueIds = new uint[4];
        readonly (int X, int Y)?[] statuePlacements = new (int X, int Y)?[4];
        readonly (int X, int Y)? cachedArenaMiddle;
        readonly HashSet<uint> adds = [];

        (int X, int Y) arenaMiddle;
        bool statuesPlaced, p1WaveSent, p2Initialized, cleaned, stopped;
        uint p2BossId;
        uint activeGuardianId;
        int p2StatuesCompleted;
        int activeStatueIndex = -1;
        double nextBeamAt, nextBulletHellAt;
        SweepingBeam? activeBeam;
        BulletHellNova? activeNova;

        public Fight(GameArea area, uint seed)
        {
            this.area = area;
            var placements = LoadAreaPlacements(Area, seed);
            cachedArenaMiddle = placements.Of("Metadata/Monsters/AvariusCasticus/ArenaMiddle").FirstOrDefault() is { } m ? (m.X, m.Y) : null;
            var spawners = placements.Of("Metadata/Monsters/AvariusCasticus/AvariusStatueSpawner");
            if (spawners.Count < 4) spawners = placements.Of(DormantStatue);
            for (int i = 0; i < 4 && i < spawners.Count; i++)
                statuePlacements[i] = (spawners[i].X, spawners[i].Y);
        }

        public void StopAndClear()
        {
            stopped = true;
            if (p2BossId != 0 && area.Monsters.TryGetValue(p2BossId, out var b))
                ClearInvulnerableOnUnload(area, p2BossId, b, CannotBeDamagedBuff);
            foreach (uint sid in dormantStatueIds)
                if (sid != 0 && area.Monsters.TryGetValue(sid, out var sm))
                    ClearInvulnerableOnUnload(area, sid, sm, CannotBeDamagedBuff);
        }

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
                await EnsureDormantStatues(runner, bossId, boss.Level, pos);

            if (!p1WaveSent && (ulong)boss.Life * 100 <= (ulong)Math.Max(1u, boss.MaxLife) * 55)
            {
                p1WaveSent = true;
                await SpawnTemplarWave(runner, bossId, boss.Level, arenaMiddle, count: 3);
            }
        }

        async Task StepInnocence(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            bool bossRebound = p2BossId != bossId;
            p2BossId = bossId;
            if (boss.LifePhases is null)
            {
                boss.LifePhases = new([80, 60, 40, 20]);
                AdvancePhases(boss.LifePhases, p2StatuesCompleted);
            }
            var pos = boss.Walk.Position(now);

            if (!statuesPlaced || bossRebound)
            {
                await EnsureDormantStatues(runner, bossId, boss.Level, pos);
                if (activeStatueIndex is >= 0 and < 4)
                {
                    // Restored from SavedMonsters mid-statue phase: ensure targetable guardian exists.
                    if (activeGuardianId == 0 || !runner.Area.Monsters.TryGetValue(activeGuardianId, out var g) || !Alive(g))
                    {
                        SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                        await AnimateStatue(runner, bossId, boss.Level, activeStatueIndex);
                    }
                }
            }

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

            if (phases.Active)
            {
                bool guardianDead = activeGuardianId == 0
                    || !runner.Area.Monsters.TryGetValue(activeGuardianId, out var gm)
                    || !Alive(gm);
                if (guardianDead)
                {
                    activeGuardianId = 0;
                    activeStatueIndex = -1;
                    SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
                    phases.Finish();
                    p2StatuesCompleted = phases.Completed;
                    Console.WriteLine($"[gateway] Innocence: animated Guardian {p2StatuesCompleted}/4 defeated; Innocence vulnerable");
                }
                return;
            }

            // Safety guarantee: never leave Innocence invulnerable outside an active statue phase.
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);

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

        async Task EnsureDormantStatues(Player runner, uint ownerId, byte level, (int X, int Y) fallbackCenter)
        {
            statuesPlaced = true;
            arenaMiddle = cachedArenaMiddle ?? fallbackCenter;
            for (int i = p2StatuesCompleted; i < 4; i++)
            {
                if (i == activeStatueIndex) continue;
                if (dormantStatueIds[i] != 0 && runner.Area.Monsters.TryGetValue(dormantStatueIds[i], out var existing) && Alive(existing))
                    continue;
                var spot = statuePlacements[i] ?? RingSpot(runner, arenaMiddle, (Math.PI / 4.0) + i * (Math.PI / 2.0), 34);
                uint sid = await SpawnAdd(runner, DormantStatue, level, spot, ownerId,
                    entranceGrace: true, rarity: MonsterRarity.Normal, ownedCurrent: () => !stopped && !cleaned);
                dormantStatueIds[i] = sid;
                if (sid != 0 && runner.Area.Monsters.TryGetValue(sid, out var sm))
                {
                    SetInvulnerable(runner, sid, sm, true, CannotBeDamagedBuff);
                    await SetMonsterTransition(runner, sid, sm, 1);
                }
            }
        }

        async Task AnimateStatue(Player runner, uint bossId, byte level, int index)
        {
            var spot = statuePlacements[index] ?? RingSpot(runner, arenaMiddle, (Math.PI / 4.0) + index * (Math.PI / 2.0), 34);
            uint dormantId = dormantStatueIds[index];
            if (dormantId != 0)
            {
                if (runner.Area.Monsters.TryGetValue(dormantId, out var dm))
                    spot = dm.Walk.Position(AreaTime(runner));
                await RetireMonsters(runner, [dormantId]);
                dormantStatueIds[index] = 0;
            }
            // Spawn a targetable Unique Guardian at the dormant statue's spot (never apply dormant buff 841!).
            activeGuardianId = await SpawnAdd(runner, AnimatedGuardian, level, spot, bossId,
                rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !cleaned);
            if (activeGuardianId == 0)
                activeGuardianId = await SpawnAdd(runner, LightborneApostle, level, spot, bossId,
                    rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !cleaned);
            Console.WriteLine($"[gateway] Innocence: animated Guardian {index + 1}/4 spawned as targetable 0x{activeGuardianId:x}");
            await SpawnTemplarWave(runner, bossId, level, arenaMiddle, count: 2);
        }

        async Task SpawnTemplarWave(Player runner, uint bossId, byte level, (int X, int Y) center, int count)
        {
            int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            var rng = AreaRng(runner);
            for (int i = 0; i < count && living + i < 6; i++)
            {
                var spot = RingSpot(runner, center, rng.NextDouble() * Math.Tau, 24);
                string kind = (i & 1) == 0 ? AnimatedGuardian : LightborneApostle;
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
                {
                    // Always key Source = p.Id so Leaving / Cleanup / CleanupOnUnload clear the exact AreaBuffs key.
                    BeamDebuffedPlayers.GetValue(p, _ => new()).Active = true;
                    p.Area.Buffs.Set(p.Id, new(DivineBeamDebuff, p.Id, U68: Buff.Seconds(2)));
                }
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
            if (activeGuardianId != 0)
            {
                await RetireMonsters(runner, [activeGuardianId]);
                activeGuardianId = 0;
            }
            await RetireMonsters(runner, dormantStatueIds.Where(id => id != 0));
            Array.Clear(dormantStatueIds);
            await RetireMonsters(runner, adds);
            adds.Clear();
            foreach (var p in ActivePlayers(runner))
                ClearBeamDebuff(p);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Innocence defeated; statues, guardians, adds, and hazards cleared");
        }
    }
}

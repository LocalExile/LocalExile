using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 10 — The Desecrated Chambers (<c>2_10_4</c>) Avarius, Reassembled (<c>AvariusCasticusWickerman[5469]</c>)
/// encounter controller (mirroring <see cref="InnocenceEncounter"/>):
/// <list type="bullet">
/// <item><b><c>[66, 33]</c> Invulnerable Statue Phases</b>: Hovers invulnerable (<c>SetInvulnerable</c> + buff 350)
/// while casting <c>AvariusRaiseStatuesWickerman[6327]</c> to animate 2× <c>AvariusCasticusStatueWickerman[5481]</c>
/// corrupted guardians per intermission (4 total, <c>NoRewards = true</c>) + Kitava cultist adds. Defeating the
/// active corrupted guardians immediately drops invulnerability. Survives portal re-entry via
/// <see cref="CampaignBossHelpers.SyncSavedPhases"/>.</item>
/// <item><b>Active Combat</b>: Radial bullet-hell nova (<c>AvariusWickermanRapidProjectile[6316]</c>), offset fire
/// volleys (<c>6288/6290</c>), telegraphed Kitava cleave (<c>AvariusCleaveWickerman[6285]</c>), and fireballs
/// (<c>23</c>).</item>
/// </list>
/// </summary>
public static class AvariusReassembledEncounter
{
    public const string Area = "2_10_4";
    public const string AvariusWickerman = "Metadata/Monsters/AvariusCasticus/AvariusCasticusWickerman";       // [5469]
    public const string CorruptedGuardian = "Metadata/Monsters/AvariusCasticus/AvariusCasticusStatueWickerman"; // [5481]
    public const string CultistDaggers = "Metadata/Monsters/KitavaCultist/KitavaCultistDaggersEvent";           // [5975]
    public const string CultistKnifestick = "Metadata/Monsters/KitavaCultist/KitavaCultistKnifestickEvent";     // [5977]

    // Verified GrantedEffects rows on AvariusCasticusWickerman[5469] in client.db:
    const int MeleeEffect = 406;
    const int RapidProjectileEffect = 6316;  // AvariusWickermanRapidProjectile
    const int FireballEffect = 23;           // AvariusWickermanFireball
    const int RaiseStatuesEffect = 6327;     // AvariusRaiseStatuesWickerman
    const int OffsetForwardEffect = 6288;    // AvariusOffsetProjectileForwardWickerman
    const int OffsetRightEffect = 6290;      // AvariusOffsetProjectileRightWickerman
    const int CleaveEffect = 6285;           // AvariusCleaveWickerman

    static readonly int[] BossEffects =
    [
        MeleeEffect, RapidProjectileEffect, FireballEffect, RaiseStatuesEffect,
        OffsetForwardEffect, OffsetRightEffect, CleaveEffect,
    ];

    const ushort CannotBeDamagedBuff = 350;  // cannot_be_damaged

    // Verified .ao visual paths in game-files.zip:
    const string ActivateStatuesKitavaArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/activate_statues/activate_statues_kitava.ao";
    const string CleaveSignalKitavaArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/cleave_signal/cleave_signal_kitava.ao";
    const string CleaveKitavaArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/cleave/cleave_kitava.ao";
    const string DashEndSlamArt = "Metadata/Effects/Spells/monsters_effects/act5/avarius_casticus/avariuscasticusdivine/dash_endslam/dash_endslam.ao";
    const string XExplosionArt = "Metadata/Effects/Spells/monsters_effects/kitava_demon/x_explosion.ao";

    const string PhaseKey = "AvariusWickerman";
    static readonly int[] PhaseThresholds = [66, 33];

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id == AvariusWickerman);
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

    public static Task Leaving(Player player) => Task.CompletedTask;

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
        }
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

    sealed record DelayedAttack(double At, uint SourceId, SpawnedMonster Source, MonsterAttack Attack,
        (int X, int Y) Origin, double Facing, (int X, int Y) Spot, double Radius, bool IsCone, string? BurstAo);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<DelayedAttack> attacks = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> activeGuardians = [];
        readonly HashSet<uint> adds = [];
        readonly (int X, int Y)?[] statueSpots = new (int X, int Y)?[4];

        (int X, int Y) arenaCenter;
        uint bossId;
        bool initialized, inStatuePhase, cleaned, stopped;
        double nextSpecial, nextBulletHell;
        int rotation;
        BulletHellNova? activeNova;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            var spots = placed.Of(CorruptedGuardian);
            for (int i = 0; i < 4 && i < spots.Count; i++)
                statueSpots[i] = (spots[i].X, spots[i].Y);
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bossId != 0 && area.Monsters.TryGetValue(bossId, out var b))
                ClearInvulnerableOnUnload(area, bossId, b, CannotBeDamagedBuff);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepDelayedAttacks(runner, now);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == AvariusWickerman);
            if (entry.Value is not { } boss) return;
            bool rebound = bossId != entry.Key;
            bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, BossEffects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);
            var phases = boss.LifePhases!;
            var pos = boss.Walk.Position(now);

            if (!initialized || rebound)
            {
                if (!initialized)
                {
                    initialized = true;
                    arenaCenter = ComputeArenaCenter(pos);
                    nextSpecial = now + 2.5;
                    nextBulletHell = now + 10.0;
                }
                if (inStatuePhase)
                {
                    if (!phases.Active) phases.Begin(0, 1);
                    SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                    if (!activeGuardians.Any(gid => runner.Area.Monsters.TryGetValue(gid, out var g) && Alive(g)))
                        await SpawnGuardianPair(runner, bossId, boss.Level, Math.Min(1, phases.Completed), now);
                }
                Console.WriteLine($"[gateway] Avarius, Reassembled controller synced for 0x{bossId:x} (statue phases done={phases.Completed}/2)");
            }

            // Active invulnerable statue phase: wait until both animated corrupted guardians are defeated.
            if (inStatuePhase || phases.Active)
            {
                bool guardiansAlive = activeGuardians.Any(gid => runner.Area.Monsters.TryGetValue(gid, out var g) && Alive(g));
                if (guardiansAlive) return;

                inStatuePhase = false;
                activeGuardians.Clear();
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
                MarkPhaseCompleted(runner.Instance, PhaseKey, phases.Completed);
                phases.Finish();
                boss.BusyUntil = now + 0.8;
                Console.WriteLine($"[gateway] Avarius, Reassembled: corrupted guardians defeated ({phases.Completed}/2); boss vulnerable");
                return;
            }

            // Safety guarantee: never leave boss invulnerable outside a statue phase.
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);

            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;
            MarkStarted(runner.Instance, PhaseKey);

            // Trigger [66, 33] statue phases.
            if (phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                inStatuePhase = true;
                activeNova = null;
                attacks.Clear();
                SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                await TeleportBoss(runner, bossId, boss, arenaCenter, now);
                await CastAction(runner, bossId, boss, BossEffects, RaiseStatuesEffect, arenaCenter, now, DamageType.Fire, 0.5, 16, "AvariusReassembled");
                await SpawnTimedEffect(runner, effects, arenaCenter, ActivateStatuesKitavaArt, now + 2.4, scale: 1.3f);
                await SpawnGuardianPair(runner, bossId, boss.Level, phases.Completed, now);
                return;
            }

            await StepBulletHell(runner, now);

            if (activeNova is not null || now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0)
                return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // Radial bullet-hell nova (AvariusWickermanRapidProjectile[6316]).
            if (now >= nextBulletHell)
            {
                nextBulletHell = now + 15.0;
                if (await CastAction(runner, bossId, boss, BossEffects, RapidProjectileEffect, aim, now, DamageType.Fire, 1.1, 10, "AvariusReassembled", forceSpell: true) is not null)
                {
                    await SpawnTimedEffect(runner, effects, pos, DashEndSlamArt, now + 3.4, scale: 1.35f);
                    activeNova = new(bossId, boss, pos, now + 0.7, now + 3.7);
                    return;
                }
            }

            if (now >= nextSpecial)
            {
                int step = rotation++;
                nextSpecial = now + 4.4;
                if (step % 3 == 0 && dist <= 26)
                {
                    // Telegraphed Kitava cleave (AvariusCleaveWickerman[6285]).
                    if (await CastAction(runner, bossId, boss, BossEffects, CleaveEffect, aim, now, DamageType.Fire, 1.55, 24, "AvariusReassembled", forceSpell: false) is { } cleave)
                    {
                        double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                        var cleaveSpot = RingSpot(runner, pos, facing, 10);
                        await SpawnTimedEffect(runner, effects, cleaveSpot, CleaveSignalKitavaArt, now + 1.2, scale: 1.25f);
                        attacks.Add(new(now + 1.15, bossId, boss, cleave.Attack, pos, facing, cleaveSpot, 24, IsCone: true, BurstAo: CleaveKitavaArt));
                        return;
                    }
                }
                else if (step % 3 == 1)
                {
                    // Offset fire projectile volleys (6288 Forward / 6290 Right).
                    int offsetEffect = (step & 1) == 0 ? OffsetForwardEffect : OffsetRightEffect;
                    if (await CastAction(runner, bossId, boss, BossEffects, offsetEffect, aim, now, DamageType.Fire, 1.2, 11, "AvariusReassembled", forceSpell: true) is { } volley)
                    {
                        double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                        for (int i = -1; i <= 1; i++)
                        {
                            double a = facing + i * 0.32;
                            var hitSpot = RingSpot(runner, pos, a, Math.Clamp(dist, 12, 34));
                            attacks.Add(new(now + 0.75 + Math.Abs(i) * 0.15, bossId, boss, volley.Attack, pos, a, hitSpot, 10, IsCone: false, BurstAo: XExplosionArt));
                        }
                        return;
                    }
                }
                else
                {
                    // AvariusWickermanFireball[23].
                    if (await CastAction(runner, bossId, boss, BossEffects, FireballEffect, aim, now, DamageType.Fire, 1.25, 12, "AvariusReassembled", forceSpell: true) is { } fb)
                    {
                        attacks.Add(new(now + 0.75, bossId, boss, fb.Attack, pos, 0, aim, 12, IsCone: false, BurstAo: XExplosionArt));
                        return;
                    }
                }
            }

            if (dist <= 16)
            {
                if (await CastAction(runner, bossId, boss, BossEffects, MeleeEffect, aim, now, DamageType.Physical, 1.05, 14, "AvariusReassembled", forceSpell: false) is { } melee)
                    attacks.Add(new(now + melee.Attack.Seconds * 0.45, bossId, boss, melee.Attack, pos, 0, aim, 13, IsCone: false, BurstAo: null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "AvariusReassembled");
            }
        }

        (int X, int Y) ComputeArenaCenter((int X, int Y) fallback)
        {
            var valid = statueSpots.OfType<(int X, int Y)>().ToArray();
            if (valid.Length == 0) return fallback;
            return ((int)Math.Round(valid.Average(v => v.X)), (int)Math.Round(valid.Average(v => v.Y)));
        }

        async Task SpawnGuardianPair(Player runner, uint ownerId, byte level, int phaseIndex, double now)
        {
            int startSlot = phaseIndex * 2;
            for (int k = 0; k < 2; k++)
            {
                int slot = (startSlot + k) % 4;
                var spot = statueSpots[slot] ?? RingSpot(runner, arenaCenter, (Math.PI / 4.0) + slot * (Math.PI / 2.0), 30);
                await SpawnTimedEffect(runner, effects, spot, ActivateStatuesKitavaArt, now + 1.8, scale: 1.15f);
                uint gid = await SpawnAdd(runner, CorruptedGuardian, level, spot, ownerId, activeGuardians,
                    rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !cleaned);
                if (gid != 0) adds.Add(gid);
            }

            int livingCultists = adds.Count(aid => !activeGuardians.Contains(aid)
                && runner.Area.Monsters.TryGetValue(aid, out var m) && Alive(m));
            var rng = AreaRng(runner);
            for (int i = 0; i < 2 && livingCultists + i < 5; i++)
            {
                var cSpot = RingSpot(runner, arenaCenter, rng.NextDouble() * Math.Tau, 22);
                string cultist = (i & 1) == 0 ? CultistDaggers : CultistKnifestick;
                await SpawnAdd(runner, cultist, level, cSpot, ownerId, adds, ownedCurrent: () => !stopped && !cleaned);
            }
            Console.WriteLine($"[gateway] Avarius, Reassembled phase {phaseIndex + 1}/2: animated {activeGuardians.Count} Corrupted Guardians + cultists");
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
            nova.NextWaveAt = now + 0.85;
            int wave = nova.WaveIndex++;

            double ringRadius = 12 + wave * 14;
            double angleOffset = wave * (Math.PI / 10.0);
            var orbs = new (int X, int Y)[10];
            for (int i = 0; i < 10; i++)
            {
                double a = angleOffset + i * (Math.Tau / 10.0);
                orbs[i] = (
                    (int)Math.Round(nova.Origin.X + Math.Cos(a) * ringRadius),
                    (int)Math.Round(nova.Origin.Y + Math.Sin(a) * ringRadius));
            }
            var atk = BuildAttack(runner, nova.Source, BossEffects, RapidProjectileEffect, DamageType.Fire, 1.15, 7, forceSpell: true);
            foreach (var p in ActivePlayers(runner))
                await ArenaHazards.Hit(p, nova.SourceId, nova.Source, atk,
                    at => orbs.Any(o => Distance(at, o) <= 6.5),
                    () => !stopped && Alive(nova.Source));
        }

        async Task StepDelayedAttacks(Player runner, double now)
        {
            for (int i = attacks.Count - 1; i >= 0; i--)
            {
                var a = attacks[i];
                if (now < a.At) continue;
                attacks.RemoveAt(i);
                if (!Alive(a.Source)) continue;
                if (a.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, a.Spot, ao, now + 1.2, scale: 1.25f);
                if (a.IsCone)
                    await HitCone(runner, a.SourceId, a.Source, a.Attack, a.Origin, a.Facing, 0.75, a.Radius, () => !stopped && Alive(a.Source));
                else
                    await HitCircle(runner, a.SourceId, a.Source, a.Attack, a.Spot, a.Radius, () => !stopped && Alive(a.Source));
            }
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            activeNova = null;
            inStatuePhase = false;
            attacks.Clear();
            if (bossId != 0 && runner.Area.Monsters.TryGetValue(bossId, out var b))
                SetInvulnerable(runner, bossId, b, false, CannotBeDamagedBuff);
            await RetireMonsters(runner, activeGuardians.Concat(adds));
            activeGuardians.Clear();
            adds.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Avarius, Reassembled defeated; statues, cultists, and hazards cleared");
        }
    }
}

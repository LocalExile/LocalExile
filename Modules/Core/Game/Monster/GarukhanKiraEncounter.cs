using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 9 — The Quarry (<c>2_9_8</c>) Kira &amp; Garukhan, Queen of the Winds encounter controller:
/// <list type="bullet">
/// <item><b>Kira (<c>KiraQuarry[6128]</c> -> hostile <c>Kira[6120]</c>)</b>: Spawns Kira inside
/// <c>boss_garukhan.arm</c> and grants <c>a9q2-KilledKira</c> on her death (matching <c>CharacterVoiceLines.cs</c>
/// in <c>2_9_8</c>).</item>
/// <item><b>Garukhan (<c>Garukhan[6103]</c>)</b>: Roaming <c>GarukhanTornadoDaemon</c> tornadoes +
/// <c>GarukhanTornadoCascade[8445]</c> triple-tornado sweeps, <c>GarukhanGrowingSandstorm[6229]</c> shrinking safe
/// zone (blinding + wind DoT outside), <c>GarukhanChargedDashMedium[6754]</c>/<c>Far[6755]</c> line sweeps,
/// <c>GarukhanAmbush[8456]</c> shadow-step ambush, and <c>Sand Warrior</c>/<c>Sand Goliath</c> add waves
/// (<c>NoRewards = true</c>), plus <c>Garukhan_Sandwall</c> arena gating.</item>
/// </list>
/// </summary>
public static class GarukhanKiraEncounter
{
    public const string Area = "2_9_8";
    public const string Garukhan = "Metadata/Monsters/Garukhan/Garukhan";                    // [6103]
    public const string Kira = "Metadata/Monsters/Kira/Kira";                                // [6120]
    public const string KiraPlacementType = "Metadata/Monsters/Kira/KiraQuarry";             // [6128]
    public const string SandWarrior = "Metadata/Monsters/Garukhan/GarukhanMinionSmall";      // [6131]
    public const string SandGoliath = "Metadata/Monsters/Garukhan/GarukhanMinionLarge";      // [6132]
    public const string KilledKiraFlag = "a9q2-KilledKira";

    // Verified GrantedEffects rows on Garukhan[6103] in client.db:
    const int MeleeEffect = 406;
    const int ComboEffect = 7857;            // GarukhanComboAttack
    const int TornadoCascadeEffect = 8445;   // GarukhanTornadoCascade
    const int AmbushEffect = 8456;           // GarukhanAmbush
    const int GrowingSandstormEffect = 6229; // GarukhanGrowingSandstorm
    const int ChargedDashMedEffect = 6754;   // GarukhanChargedDashMedium
    const int ChargedDashFarEffect = 6755;   // GarukhanChargedDashFar
    const int MinionsEffect = 7863;          // EmptyActionSpellGarukhanMinions

    static readonly int[] GarukhanEffects =
    [
        MeleeEffect, ComboEffect, TornadoCascadeEffect, AmbushEffect,
        GrowingSandstormEffect, ChargedDashMedEffect, ChargedDashFarEffect, MinionsEffect,
    ];

    // Verified BuffDefinitions rows in client.db:
    const ushort SandstormBuff = 890;        // ground_sandstorm ("Garukhan's Sandstorm")
    const ushort BlindedBuff = 179;          // blinded

    // Verified .ao visual paths in game-files.zip:
    const string TornadoArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/large_tornado/large_tornado.ao";
    const string SandstormArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/sandstorm/rig.ao";
    const string DashBuildupArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/charged_dash/buildup/buildup.ao";
    const string DashAoeArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/charged_dash/aoe/aoe.ao";
    const string AmbushSlashArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/ambush/ambush_slash.ao";
    const string DisappearArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/disappear/disappear.ao";
    const string KiraEmergeArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/kira/emerge/emerge.ao";

    // Verified object_types in client.db: state 2 = blocking sandwall, state 3 = open.
    const string SandwallGateType = "Metadata/QuestObjects/Act9/Garukhan_Sandwall";
    const byte GateLockedState = 2, GateUnlockedState = 3;

    const string PhaseKey = "Garukhan";
    static readonly int[] PhaseThresholds = [66, 33];

    static readonly IReadOnlyDictionary<uint, int> SandstormDebuffStats = new Dictionary<uint, int>
    {
        [Stat.BaseMovementVelocityPlusPct] = -25,
    };

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id is Garukhan or Kira);
            if (!present) return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player));
        }
        await fight.Gate.WaitAsync();
        try
        {
            SyncGatesForPlayer(player, fight.Gates);
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static async Task OnMonsterKilled(MonsterKill kill)
    {
        if (kill.Player.Layout.Id != Area || kill.Monster.VarietyId != Kira) return;
        await GrantKiraKilledFlags(kill.Player);
    }

    public static async Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return;
        if (Fights.TryGetValue(player.Instance, out var fight))
        {
            ReleaseGatesForPlayer(player, fight.Gates);
            await fight.ReleasePlayerDebuffs(player);
        }
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            ReleaseAllGatesOnUnload();
        }
    }

    static async Task GrantKiraKilledFlags(Player runner)
    {
        foreach (var p in ActivePlayers(runner))
        {
            if (!ClientHasQuestFlag(p, KilledKiraFlag))
            {
                await SetQuestFlags(p, [KilledKiraFlag]);
                await UpdateQuestObjectStates(p, [KilledKiraFlag]);
            }
        }
    }

    sealed class RoamingTornado(uint fxId, (int X, int Y) at, double vx, double vy, double endsAt)
    {
        public uint FxId = fxId;
        public double X = at.X, Y = at.Y;
        public double Vx = vx, Vy = vy;
        public readonly double EndsAt = endsAt;
        public double NextHitAt;
        public double NextVisualAt;
    }

    sealed record DelayedSweep(double At, int Gen, uint SourceId, SpawnedMonster Source, MonsterAttack Attack,
        (int X, int Y) From, (int X, int Y) To, double HalfWidth, bool IsLine, string? BurstAo);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<ArenaGate> Gates = [];

        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<RoamingTornado> tornadoes = [];
        readonly List<DelayedSweep> sweeps = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> adds = [];
        readonly Dictionary<Player, ArenaHazards.Exposure> stormExposures = [];
        readonly object stormHolder = new();
        readonly object blindHolder = new();

        readonly (int X, int Y)? kiraPlacement;
        readonly (int X, int Y)? spawnerCenter;
        readonly List<(int X, int Y)> minionSpawners;
        readonly List<GlyphQuest.Placement> sandwallPlacements;

        (int X, int Y) arenaCenter;
        uint garukhanId, kiraId;
        bool gatesInit, gatesLocked, kiraSpawned, kiraDefeated;
        bool initialized, cleaned, stopped, sandstormActive;
        double safeRadius = 46;
        double nextSpecial, nextTornado, nextAddWave, nextTornadoStep, nextStormTick;
        int rotation;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            kiraPlacement = placed.Of(KiraPlacementType).FirstOrDefault() is { } kp ? (kp.X, kp.Y) : null;
            spawnerCenter = placed.Of("Metadata/Monsters/Garukhan/Spawners/SpawnerController").FirstOrDefault() is { } sc ? (sc.X, sc.Y) : null;
            string[] spawnerTypes =
            [
                "Metadata/Monsters/Garukhan/Spawners/Spawner1A",
                "Metadata/Monsters/Garukhan/Spawners/Spawner1B",
                "Metadata/Monsters/Garukhan/Spawners/Spawner1C",
                "Metadata/Monsters/Garukhan/Spawners/Spawner2A",
                "Metadata/Monsters/Garukhan/Spawners/Spawner2B",
                "Metadata/Monsters/Garukhan/Spawners/Spawner2C",
            ];
            minionSpawners = [.. spawnerTypes.SelectMany(placed.Of).Select(p => (p.X, p.Y))];
            sandwallPlacements = placed.Of(SandwallGateType);
        }

        public void StopAndClear()
        {
            stopped = true;
            foreach (var p in stormExposures.Keys.ToArray())
            {
                _ = ReleasePlayerDebuff(p, stormHolder);
                _ = ReleasePlayerDebuff(p, blindHolder);
            }
            stormExposures.Clear();
        }

        public async Task ReleasePlayerDebuffs(Player p)
        {
            stormExposures.Remove(p);
            await ReleasePlayerDebuff(p, stormHolder);
            await ReleasePlayerDebuff(p, blindHolder);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepSweeps(runner, now);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Garukhan);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;
            garukhanId = bossId;

            if (!gatesInit)
            {
                gatesInit = true;
                foreach (var gp in sandwallPlacements)
                    if (await CreateGate(runner, SandwallGateType, gp, startLocked: false, GateLockedState, GateUnlockedState) is { } g)
                        Gates.Add(g);
            }

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, GarukhanEffects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);
            var phases = boss.LifePhases!;
            var pos = boss.Walk.Position(now);
            arenaCenter = spawnerCenter ?? pos;

            // Spawn Kira in boss_garukhan.arm when players enter the arena (unless already slain).
            var target = NearestLivingTarget(runner, arenaCenter, now, maxRange: 140, awakeId: bossId);
            if (target is null)
            {
                if (gatesLocked)
                {
                    gatesLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                }
                return;
            }

            if (!kiraSpawned)
            {
                kiraSpawned = true;
                bool alreadyKilled = ActivePlayers(runner).All(p => ClientHasQuestFlag(p, KilledKiraFlag));
                if (!alreadyKilled)
                {
                    var kSpot = kiraPlacement ?? RingSpot(runner, arenaCenter, 1.2, 16);
                    await SpawnTimedEffect(runner, effects, kSpot, KiraEmergeArt, now + 2.0, scale: 1.1f);
                    // Do NOT track Kira in adds: killing Garukhan first must leave Kira alive and fightable for a9q2-KilledKira.
                    kiraId = await SpawnAdd(runner, Kira, boss.Level, kSpot, bossId, track: null,
                        rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped);
                    Console.WriteLine($"[gateway] Quarry arena: spawned Kira 0x{kiraId:x} alongside Garukhan 0x{bossId:x}");
                }
                else
                {
                    kiraDefeated = true;
                }
            }
            else if (!kiraDefeated && kiraId != 0 && (!runner.Area.Monsters.TryGetValue(kiraId, out var km) || !Alive(km)))
            {
                kiraDefeated = true;
                await GrantKiraKilledFlags(runner);
                Console.WriteLine("[gateway] Kira defeated in Quarry; a9q2-KilledKira granted");
            }

            if (!gatesLocked)
            {
                gatesLocked = true;
                foreach (var g in Gates) await SetGateLocked(runner, g, true);
            }

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;
            MarkStarted(runner.Instance, PhaseKey);

            if (!initialized)
            {
                initialized = true;
                sandstormActive = true;
                safeRadius = phases.Completed >= 2 ? 24 : phases.Completed == 1 ? 34 : 44;
                nextSpecial = now + 2.5;
                nextTornado = now + 5.5;
                nextAddWave = now + 9.0;
                await SpawnTimedEffect(runner, effects, arenaCenter, SandstormArt, now + 600, scale: 1.6f);
            }

            // Phase transitions at 66% / 33%: shrink GarukhanGrowingSandstorm[6229] safe zone & summon Sand Warrior/Goliath wave.
            if (phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                int idx = phases.Completed;
                safeRadius = idx == 0 ? 33 : 23;
                await CastAction(runner, bossId, boss, GarukhanEffects, GrowingSandstormEffect, arenaCenter, now, DamageType.Physical, 0.8, 22, "Garukhan");
                await SpawnSandWave(runner, bossId, boss.Level, idx == 0 ? 3 : 4);
                await SpawnTornadoCascade(runner, bossId, boss, target.Walk.Position(now), now);
                MarkPhaseCompleted(runner.Instance, PhaseKey, idx);
                phases.Finish();
                Console.WriteLine($"[gateway] Garukhan phase {phases.Completed}/2: sandstorm safe radius shrunk to {safeRadius}");
            }

            await StepTornadoes(runner, bossId, boss, now);
            await StepSandstorm(runner, bossId, boss, now);

            if (now >= nextTornado && tornadoes.Count < 4)
            {
                nextTornado = now + 7.5;
                await SpawnRoamingTornado(runner, pos, target.Walk.Position(now), now);
            }

            if (now >= nextAddWave)
            {
                nextAddWave = now + 15.0;
                await SpawnSandWave(runner, bossId, boss.Level, 2);
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            if (now >= nextSpecial)
            {
                int step = rotation++;
                nextSpecial = now + (phases.Completed >= 2 ? 3.4 : 4.3);
                if (step % 4 == 0)
                {
                    await CastTornadoCascade(runner, bossId, boss, aim, now);
                    return;
                }
                if (step % 4 == 1)
                {
                    int dashEffect = dist > 32 ? ChargedDashFarEffect : ChargedDashMedEffect;
                    await CastChargedDash(runner, bossId, boss, dashEffect, aim, now);
                    return;
                }
                if (step % 4 == 2)
                {
                    await CastAmbush(runner, bossId, boss, aim, now);
                    return;
                }
                if (await CastAction(runner, bossId, boss, GarukhanEffects, ComboEffect, aim, now, DamageType.Physical, 1.35, 16, "Garukhan", forceSpell: false) is { } combo)
                {
                    sweeps.Add(new(now + 0.65, combo.Generation, bossId, boss, combo.Attack, pos, aim, 15, IsLine: false, BurstAo: AmbushSlashArt));
                    sweeps.Add(new(now + 1.35, combo.Generation, bossId, boss, combo.Attack, pos, aim, 16, IsLine: false, BurstAo: null));
                    return;
                }
            }

            if (dist <= 15)
            {
                if (await CastAction(runner, bossId, boss, GarukhanEffects, MeleeEffect, aim, now, DamageType.Physical, 1.0, 14, "Garukhan", forceSpell: false) is { } melee)
                    sweeps.Add(new(now + melee.Attack.Seconds * 0.45, melee.Generation, bossId, boss, melee.Attack, pos, aim, 13, IsLine: false, BurstAo: null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "Garukhan");
            }
        }

        async Task CastTornadoCascade(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) aim, double now)
        {
            if (await CastAction(runner, bossId, boss, GarukhanEffects, TornadoCascadeEffect, aim, now, DamageType.Physical, 1.15, 12, "Garukhan") is null)
                return;
            await SpawnTornadoCascade(runner, bossId, boss, aim, now);
        }

        async Task SpawnTornadoCascade(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) aim, double now)
        {
            var pos = boss.Walk.Position(now);
            double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
            for (int i = -1; i <= 1; i++)
            {
                double angle = facing + i * 0.42;
                var dirGoal = (pos.X + (int)Math.Round(Math.Cos(angle) * 40), pos.Y + (int)Math.Round(Math.Sin(angle) * 40));
                await SpawnRoamingTornado(runner, pos, dirGoal, now, duration: 7.0, speed: 12.0);
            }
        }

        async Task SpawnRoamingTornado(Player runner, (int X, int Y) start, (int X, int Y) toward, double now, double duration = 9.0, double speed = 9.0)
        {
            double angle = Math.Atan2(toward.Y - start.Y, toward.X - start.X);
            uint fxId = await SpawnTimedEffect(runner, effects, start, TornadoArt, now + 1.6, scale: 1.15f);
            tornadoes.Add(new(fxId, start, Math.Cos(angle) * speed, Math.Sin(angle) * speed, now + duration)
            {
                NextHitAt = now + 0.4,
                NextVisualAt = now + 1.2,
            });
        }

        async Task StepTornadoes(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            if (tornadoes.Count == 0 || now < nextTornadoStep) return;
            double dt = nextTornadoStep == 0 ? 0.25 : Math.Clamp(now - nextTornadoStep, 0.05, 0.5);
            nextTornadoStep = now + 0.25;

            var atk = BuildAttack(runner, boss, GarukhanEffects, TornadoCascadeEffect, DamageType.Physical, 0.85, 9, forceSpell: true);
            for (int i = tornadoes.Count - 1; i >= 0; i--)
            {
                var t = tornadoes[i];
                if (now >= t.EndsAt)
                {
                    tornadoes.RemoveAt(i);
                    continue;
                }
                t.X += t.Vx * dt;
                t.Y += t.Vy * dt;
                var spot = WalkableNear(runner, ((int)Math.Round(t.X), (int)Math.Round(t.Y)));
                if (now >= t.NextVisualAt)
                {
                    t.NextVisualAt = now + 1.2;
                    t.FxId = await SpawnTimedEffect(runner, effects, spot, TornadoArt, Math.Min(t.EndsAt, now + 1.5), scale: 1.15f);
                }
                if (now >= t.NextHitAt)
                {
                    t.NextHitAt = now + 0.65;
                    await HitCircle(runner, bossId, boss, atk, spot, 9.5, () => !stopped && Alive(boss));
                }
            }
        }

        async Task CastChargedDash(Player runner, uint bossId, SpawnedMonster boss, int effect, (int X, int Y) aim, double now)
        {
            var pos = boss.Walk.Position(now);
            if (await CastAction(runner, bossId, boss, GarukhanEffects, effect, aim, now, DamageType.Physical, 1.45, 14, "Garukhan", forceSpell: false) is not { } cast)
                return;
            await SpawnTimedEffect(runner, effects, pos, DashBuildupArt, now + 1.1, scale: 1.2f);
            await SpawnTimedEffect(runner, effects, aim, DashAoeArt, now + 1.8, scale: 1.25f);
            sweeps.Add(new(now + 1.15, cast.Generation, bossId, boss, cast.Attack, pos, aim, 9.0, IsLine: true, BurstAo: DashAoeArt));
        }

        async Task CastAmbush(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) aim, double now)
        {
            var pos = boss.Walk.Position(now);
            if (await CastAction(runner, bossId, boss, GarukhanEffects, AmbushEffect, aim, now, DamageType.Physical, 1.35, 14, "Garukhan", forceSpell: false) is not { } cast)
                return;
            await SpawnTimedEffect(runner, effects, pos, DisappearArt, now + 1.0, scale: 1.1f);
            double behind = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
            var ambushSpot = RingSpot(runner, aim, behind, 6);
            await TeleportBoss(runner, bossId, boss, ambushSpot, now);
            sweeps.Add(new(now + 0.75, cast.Generation, bossId, boss, cast.Attack, ambushSpot, aim, 13.0, IsLine: false, BurstAo: AmbushSlashArt));
        }

        async Task SpawnSandWave(Player runner, uint bossId, byte level, int count)
        {
            int living = adds.Count(id => id != kiraId && runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            var rng = AreaRng(runner);
            for (int i = 0; i < count && living + i < 6; i++)
            {
                var spot = minionSpawners.Count > 0
                    ? minionSpawners[rng.Next(minionSpawners.Count)]
                    : RingSpot(runner, arenaCenter, rng.NextDouble() * Math.Tau, 22);
                string kind = i == 0 && count >= 3 ? SandGoliath : SandWarrior;
                await SpawnAdd(runner, kind, level, spot, bossId, adds, ownedCurrent: () => !stopped && !cleaned);
            }
        }

        async Task StepSweeps(Player runner, double now)
        {
            for (int i = sweeps.Count - 1; i >= 0; i--)
            {
                var s = sweeps[i];
                if (now < s.At) continue;
                sweeps.RemoveAt(i);
                bool Current() => !stopped && Alive(s.Source) && s.Source.ActionGeneration == s.Gen;
                if (!Current()) continue;
                if (s.IsLine)
                {
                    await TeleportBoss(runner, s.SourceId, s.Source, s.To, now);
                    await HitSegment(runner, s.SourceId, s.Source, s.Attack, s.From, s.To, s.HalfWidth, Current);
                }
                else
                {
                    await HitCircle(runner, s.SourceId, s.Source, s.Attack, s.To, s.HalfWidth, Current);
                }
                if (s.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, s.To, ao, now + 1.2, scale: 1.2f);
            }
        }

        async Task StepSandstorm(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            if (!sandstormActive || now < nextStormTick) return;
            nextStormTick = now + 0.3;
            var (minDmg, maxDmg) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (28, 42);
            double stormDps = (minDmg + maxDmg) * 0.55;

            foreach (var p in ActivePlayers(runner))
            {
                var at = p.Walk.Position(now);
                double dist = Distance(at, arenaCenter);
                bool inStorm = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && dist > safeRadius && dist <= 135;
                if (inStorm)
                {
                    await HoldPlayerDebuff(p, stormHolder, SandstormBuff, bossId, SandstormDebuffStats);
                    await HoldPlayerDebuff(p, blindHolder, BlindedBuff, bossId, EmptyStats);
                }
                else
                {
                    await ReleasePlayerDebuff(p, stormHolder);
                    await ReleasePlayerDebuff(p, blindHolder);
                }

                if (!stormExposures.TryGetValue(p, out var exp))
                    stormExposures[p] = exp = new();
                await ArenaHazards.Ground(p, exp, now, (pt, _) =>
                {
                    double d = Distance(pt, arenaCenter);
                    return d > safeRadius && d <= 135
                        ? DamagePacket.Of(DamageType.Physical, stormDps, stormDps)
                        : DamagePacket.Empty;
                }, () => !stopped && !cleaned);
            }
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            sandstormActive = false;
            gatesLocked = false;
            tornadoes.Clear();
            sweeps.Clear();
            foreach (var g in Gates) await SetGateLocked(runner, g, false);
            await RetireMonsters(runner, adds);
            adds.Clear();
            foreach (var p in ActivePlayers(runner))
                await ReleasePlayerDebuffs(p);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Garukhan defeated; sandstorm, tornadoes, gates, and adds cleared");
        }
    }
}

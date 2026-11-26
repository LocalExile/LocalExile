using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 7 — The Ashen Fields (<c>2_7_7</c>) Ralakesh possession encounter covering all 4 avatars
/// (<c>Greust[5724]</c>, or spared Act 2 bandit <c>Oak[5731]</c>, <c>Alira[5734]</c>, <c>Kraityn[5737]</c>):
/// <list type="bullet">
/// <item><b>50% HP <c>RalakeshBlessing[8450]</c> Possession</b>: Brief invulnerable transform (<c>ralakesh_buff</c> 892),
/// life recovery (+25% max life), and summoning spirit beasts (<c>BearBandit2[89]</c>, <c>BoarBandit2[93]</c>,
/// <c>StagBandit2[84]</c>) or avatar bandit waves (<c>NoRewards = true</c>).</item>
/// <item><b>Empowered Blessing skills for all 4 avatars</b>: Greust (<c>GreustCharge[4364]</c>,
/// <c>GreustSpearThrow[4781]</c>, <c>GreustSpearBarrage[5623]</c>), Oak (<c>OakSunder[8853]</c>→<c>OakSunderBlessing[8854]</c>,
/// <c>OakLeapSlam[846]</c>, <c>ImmortalCallOak[310]</c>), Alira (<c>AliraVolatileDead[8754]</c>→<c>AliraVolatileDeadBlessing[8755]</c>,
/// <c>DesecrateAliraBlessing[3265]</c>), Kraityn (<c>BanditFlickerStrike[1551]</c>, <c>PhaseRunKraityn[1095]</c>,
/// <c>KraitynReave[2672]</c>, <c>ChargedAttackBanditBoss[5884]</c>).</item>
/// </list>
/// </summary>
public static class RalakeshEncounter
{
    public const string Area = "2_7_7";
    public const string Greust = "Metadata/Monsters/Ralakesh/Greust/Greust";         // MV 5724
    public const string Oak = "Metadata/Monsters/Ralakesh/Oak/Oak";                  // MV 5731
    public const string Alira = "Metadata/Monsters/Ralakesh/Alira/Alira";            // MV 5734
    public const string Kraityn = "Metadata/Monsters/Ralakesh/Kraityn/Kraityn";      // MV 5737

    public const string BearSpirit = "Metadata/Monsters/Bandits/BearBandit2";        // MV 89
    public const string BoarSpirit = "Metadata/Monsters/Bandits/BoarBandit2";        // MV 93
    public const string StagSpirit = "Metadata/Monsters/Bandits/StagBandit2";        // MV 84

    const string GreustArcher = "Metadata/Monsters/Ralakesh/Greust/GreustArcher";
    const string GreustBandit = "Metadata/Monsters/Ralakesh/Greust/GreustMeleeBandit";
    const string OakArcher = "Metadata/Monsters/Ralakesh/Oak/OakArcher";
    const string OakBandit = "Metadata/Monsters/Ralakesh/Oak/OakMeleeBandit";
    const string AliraArcher = "Metadata/Monsters/Ralakesh/Alira/AliraArcher_";
    const string AliraBandit = "Metadata/Monsters/Ralakesh/Alira/AliraMeleeBandit";
    const string KraitynArcher = "Metadata/Monsters/Bandits/BanditBowKraitynSpecific";
    const string KraitynBandit = "Metadata/Monsters/Bandits/BanditMeleePunctureKraitynSpecific";

    const string PhaseKey = "Ralakesh";
    static readonly int[] PhaseThresholds = [50];
    const ushort RalakeshBuffRow = 892; // ralakesh_buff
    const string BlessingStatsKey = "ralakesh_blessing_empower";

    static readonly IReadOnlyDictionary<uint, int> BlessingEmpowerStats = new Dictionary<uint, int>
    {
        [Stat.AttackSpeedPlusPct] = 30,
        [Stat.CastSpeedPlusPct] = 30,
        [Stat.BaseMovementVelocityPlusPct] = 20,
        [Stat.PhysicalDamagePlusPct] = 25,
    };

    const string BuffActivateArt = "Metadata/Effects/Spells/monsters_effects/act7/ralakesh_totem/ralakesh_buff_activate.ao";
    const string BuffActiveArt = "Metadata/Effects/Spells/monsters_effects/act7/ralakesh_totem/ralakesh_buff_active.ao";
    const string SpearExplodeArt = "Metadata/Effects/Spells/monsters_effects/act7/ralakesh_totem/greustspearexplode.ao";
    const string SpearProjArt = "Metadata/Effects/Spells/monsters_effects/act7/ralakesh_totem/greust_buffed_projectile/rig.ao";
    const string VolatileOrbArt = "Metadata/Effects/Spells/monsters_effects/act7/ralakesh_totem/alira_volatile_dead/volatiledead_fireorb.ao";
    const string VolatileAoeArt = "Metadata/Effects/Spells/monsters_effects/act7/ralakesh_totem/alira_volatile_dead/volatiledead_aoe.ao";
    const string DesecrateArt = "Metadata/Effects/Spells/desecrate/center.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> DesecrateExposures = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && IsAvatar(m.Variety?.Id)))
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

    static bool IsAvatar(string? id) => id is Greust or Oak or Alira or Kraityn;

    public static Task Leaving(Player player)
    {
        if (DesecrateExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in DesecrateExposures) pair.Value.Last = double.NaN;
        }
    }

    sealed record DesecratePatch((int X, int Y) At, double Ends, double Radius);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<(int X, int Y)> spawnerSpots;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<DesecratePatch> desecrates = [];
        readonly HashSet<uint> adds = [];

        uint bossId;
        bool initialized, transforming, blessed, cleaned, stopped;
        double transformEndsAt, immortalEndsAt, nextChaseAt, nextPrimaryAt, nextSecondaryAt, nextMobilityAt, nextAddAt;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            spawnerSpots = [.. placed.Of("Metadata/Monsters/Ralakesh/BanditSpawnLocator")
                .Concat(placed.Of("Metadata/Monsters/Ralakesh/ArcherSpawnLocator"))
                .Concat(placed.Of("Metadata/Monsters/Ralakesh/EliteSpawnLocator"))
                .Select(p => (p.X, p.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bossId != 0 && area.Monsters.TryGetValue(bossId, out var b))
            {
                ClearInvulnerableOnUnload(area, bossId, b, CannotBeDamagedBuff);
                b.RemoveTemporaryStats(BlessingStatsKey);
                b.RebuildStats();
                area.Buffs.Clear(bossId, RalakeshBuffRow, bossId);
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            desecrates.RemoveAll(d => now >= d.Ends);

            // Read the avatar by its remembered id first; scan the area only when that lookup is gone.
            SpawnedMonster? boss = null;
            if (bossId != 0 && runner.Area.Monsters.TryGetValue(bossId, out var cached) && cached.Hostile && IsAvatar(cached.Variety?.Id))
                boss = cached;
            else
            {
                var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && IsAvatar(p.Value.Variety?.Id));
                if (entry.Value is { } found)
                {
                    bossId = entry.Key;
                    boss = found;
                }
            }
            if (boss is null)
                return;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner, boss);
                return;
            }

            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);
            string avatar = boss.Variety!.Id;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);
            if (target is null)
            {
                ResetExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                boss.Aggro = true;
                MarkStarted(runner.Instance, PhaseKey);
                await BindEffects(runner, bossId, boss, AvatarEffects(avatar));
                await EnsureBossBar(runner, bossId, boss);
                if (boss.LifePhases!.Completed > 0)
                    ApplyBlessingEmpower(runner, boss);
                nextPrimaryAt = now + 3.5;
                nextSecondaryAt = now + 6.5;
                nextMobilityAt = now + 5.0;
                nextAddAt = now + 10.0;
                Console.WriteLine($"[gateway] Ralakesh controller active for {boss.Variety.Name} (0x{bossId:x}, blessed={blessed})");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, bossId, boss);
            }

            await StepDesecrateGround(runner, boss, now);

            // End Oak's brief Immortal Call invulnerability
            if (immortalEndsAt > 0 && now >= immortalEndsAt)
            {
                immortalEndsAt = 0;
                if (!transforming)
                    SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            }

            // Finish 50% RalakeshBlessing[8450] transformation
            if (transforming)
            {
                if (now < transformEndsAt) return;
                transforming = false;
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
                ApplyBlessingEmpower(runner, boss);
                uint healTo = (uint)Math.Min((ulong)boss.MaxLife, (ulong)boss.Life + (ulong)Math.Max(1, boss.MaxLife / 4));
                await HealBoss(runner, bossId, boss, healTo, "[Ralakesh] Blessing heal");
                await SpawnPossessionWave(runner, boss, avatar, pos);
                MarkPhaseCompleted(runner.Instance, PhaseKey, 0);
                boss.LifePhases!.Finish();
                nextPrimaryAt = now + 1.5;
                nextSecondaryAt = now + 3.5;
                Console.WriteLine($"[gateway] Ralakesh: {boss.Variety.Name} possessed — empowered Blessing skills active");
                return;
            }

            // Trigger 50% HP RalakeshBlessing[8450]
            var phases = boss.LifePhases!;
            if (!blessed && !phases.Active && phases.Completed < 1 && phases.Begin(boss.Life, boss.MaxLife))
            {
                transforming = true;
                transformEndsAt = now + 2.0;
                SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                await CastAction(runner, bossId, boss, 8450, pos, 2.0, now, "[Ralakesh] Blessing possession");
                await SpawnTimedEffect(runner, effects, pos, BuffActivateArt, now + 2.4, scale: 1.35f);
                return;
            }

            if (immortalEndsAt == 0 && boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);

            if (!CanAct(boss, now)) return;

            // Periodic reinforcement adds while possessed
            if (blessed && now >= nextAddAt)
            {
                nextAddAt = now + 15.0;
                int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (living < 4)
                    await SpawnAvatarBandits(runner, boss, avatar, pos, count: 2);
            }

            switch (avatar)
            {
                case Greust: await StepGreust(runner, boss, target, now); break;
                case Oak: await StepOak(runner, boss, target, now); break;
                case Alira: await StepAlira(runner, boss, target, now); break;
                case Kraityn: await StepKraityn(runner, boss, target, now); break;
            }
        }

        static int[] AvatarEffects(string avatar) => avatar switch
        {
            Greust => [406, 4364, 4781, 5586, 8450, 5623],
            Oak => [406, 128, 310, 271, 846, 8853, 8450, 8854],
            Alira => [406, 6026, 8754, 8450, 3265, 8755],
            _ => [406, 319, 1551, 1095, 8450, 5884, 2672],
        };

        void ApplyBlessingEmpower(Player runner, SpawnedMonster boss)
        {
            blessed = true;
            boss.SetTemporaryStats(BlessingStatsKey, BlessingEmpowerStats);
            boss.RebuildStats();
            runner.Area.Buffs.Set(bossId, new(RalakeshBuffRow, bossId));
        }

        async Task StepGreust(Player runner, SpawnedMonster boss, Player target, double now)
        {
            var pos = boss.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // Empowered GreustSpearBarrage[5623] (or GreustSpearThrow[4781])
            if (now >= nextPrimaryAt && dist <= 65)
            {
                nextPrimaryAt = now + (blessed ? 5.0 : 6.5);
                int eff = blessed ? 5623 : 4781;
                if (await CastAction(runner, bossId, boss, eff, aim, 1.25, now, "[Greust] Spear Barrage") is { } gen)
                {
                    int spears = blessed ? 5 : 3;
                    double baseAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = PhysicalAttack(boss, blessed ? 1.15 : 0.95, 10);
                    for (int i = 0; i < spears; i++)
                    {
                        double offset = (i - (spears - 1) / 2.0) * 0.22;
                        double a = baseAngle + offset;
                        var drop = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(a) * Math.Min(42, dist + 6)),
                            (int)Math.Round(pos.Y + Math.Sin(a) * Math.Min(42, dist + 6))));
                        await SpawnTimedEffect(runner, effects, drop, SpearProjArt, now + 0.8 + i * 0.08, scale: 1.0f);
                        hits.Add(new(now + 0.65 + i * 0.08, bossId, boss, drop, 9, atk, gen, SpearExplodeArt, 1.05f));
                    }
                    return;
                }
            }

            // GreustCharge[4364]
            if (now >= nextMobilityAt && dist is >= 14 and <= 55)
            {
                nextMobilityAt = now + (blessed ? 7.0 : 9.0);
                var dest = WalkableNear(runner, aim);
                if (await CastAction(runner, bossId, boss, 4364, dest, 1.1, now, "[Greust] Charge") is { } gen)
                {
                    boss.Walk.Teleport(dest.X, dest.Y, now);
                    await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, dest.X, dest.Y), null);
                    hits.Add(new(now + 0.55, bossId, boss, dest, 13, PhysicalAttack(boss, blessed ? 1.35 : 1.1, 13), gen, SpearExplodeArt, 1.15f));
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: blessed ? 1.15 : 1.0);
        }

        async Task StepOak(Player runner, SpawnedMonster boss, Player target, double now)
        {
            var pos = boss.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // OakSunderBlessing[8854] (or OakSunder[8853])
            if (now >= nextPrimaryAt && dist <= 52)
            {
                nextPrimaryAt = now + (blessed ? 5.0 : 6.8);
                int eff = blessed ? 8854 : 8853;
                if (await CastAction(runner, bossId, boss, eff, aim, 1.3, now, "[Oak] Sunder") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = PhysicalAttack(boss, blessed ? 1.35 : 1.05, 11);
                    int waves = blessed ? 4 : 3;
                    for (int s = 1; s <= waves; s++)
                    {
                        var burst = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(angle) * (s * 11)),
                            (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 11))));
                        hits.Add(new(now + 0.45 + s * 0.2, bossId, boss, burst, 10.5, atk, gen, SpearExplodeArt, 1.1f));
                    }
                    return;
                }
            }

            // OakLeapSlam[846]
            if (now >= nextMobilityAt && dist is >= 14 and <= 54)
            {
                nextMobilityAt = now + (blessed ? 7.5 : 9.5);
                var landing = WalkableNear(runner, aim);
                if (await CastAction(runner, bossId, boss, 846, landing, 1.2, now, "[Oak] Leap Slam") is { } gen)
                {
                    boss.Walk.Teleport(landing.X, landing.Y, now);
                    await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, landing.X, landing.Y), null);
                    hits.Add(new(now + 0.65, bossId, boss, landing, 14, PhysicalAttack(boss, 1.3, 14), gen, BuffActiveArt, 1.15f));
                    return;
                }
            }

            // ImmortalCallOak[310] when blessed
            if (blessed && now >= nextSecondaryAt)
            {
                nextSecondaryAt = now + 14.0;
                if (await CastAction(runner, bossId, boss, 310, pos, 0.7, now, "[Oak] Immortal Call") is not null)
                {
                    immortalEndsAt = now + 2.5;
                    SetInvulnerable(runner, bossId, boss, true, CannotBeDamagedBuff);
                    await SpawnTimedEffect(runner, effects, pos, BuffActiveArt, now + 2.5, scale: 1.2f);
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: blessed ? 1.2 : 1.05, meleeEffect: 128);
        }

        async Task StepAlira(Player runner, SpawnedMonster boss, Player target, double now)
        {
            var pos = boss.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // DesecrateAliraBlessing[3265] caustic ground
            if (now >= nextSecondaryAt && dist <= 65)
            {
                nextSecondaryAt = now + (blessed ? 6.5 : 9.0);
                if (await CastAction(runner, bossId, boss, 3265, aim, 1.0, now, "[Alira] Desecrate") is not null)
                {
                    var spot = WalkableNear(runner, aim);
                    await SpawnTimedEffect(runner, effects, spot, DesecrateArt, now + 8.0, scale: 1.2f);
                    desecrates.Add(new(spot, now + 8.0, 11.0));
                    return;
                }
            }

            // AliraVolatileDeadBlessing[8755] / AliraVolatileDead[8754]
            if (now >= nextPrimaryAt && dist <= 70)
            {
                nextPrimaryAt = now + (blessed ? 4.8 : 6.5);
                int eff = blessed ? 8755 : 8754;
                if (await CastAction(runner, bossId, boss, eff, aim, 1.2, now, "[Alira] Volatile Dead") is { } gen)
                {
                    int orbs = blessed ? 4 : 2;
                    var rng = AreaRng(runner);
                    var atk = ElementalSpellAttack(boss, DamageType.Fire, blessed ? 1.15 : 0.95, 10.5);
                    for (int i = 0; i < orbs; i++)
                    {
                        var drop = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, i == 0 ? 0 : 5 + i * 3);
                        await SpawnTimedEffect(runner, effects, drop, VolatileOrbArt, now + 1.1 + i * 0.2, scale: 1.05f);
                        hits.Add(new(now + 1.05 + i * 0.2, bossId, boss, drop, 10, atk, gen, VolatileAoeArt, 1.1f));
                    }
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: 0.95);
        }

        async Task StepKraityn(Player runner, SpawnedMonster boss, Player target, double now)
        {
            var pos = boss.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // BanditFlickerStrike[1551] teleport strike
            if (now >= nextMobilityAt && dist is >= 10 and <= 58)
            {
                nextMobilityAt = now + (blessed ? 5.0 : 7.5);
                var strikeSpot = RingSpot(runner, aim, AreaRng(runner).NextDouble() * Math.Tau, 5);
                if (await CastAction(runner, bossId, boss, 1551, strikeSpot, 0.85, now, "[Kraityn] Flicker Strike") is { } gen)
                {
                    boss.Walk.Teleport(strikeSpot.X, strikeSpot.Y, now);
                    await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, strikeSpot.X, strikeSpot.Y), null);
                    hits.Add(new(now + 0.35, bossId, boss, aim, 12, PhysicalAttack(boss, blessed ? 1.3 : 1.05, 12), gen));
                    return;
                }
            }

            // KraitynReave[2672] / ChargedAttackBanditBoss[5884]
            if (now >= nextPrimaryAt && dist <= 32)
            {
                nextPrimaryAt = now + (blessed ? 4.8 : 6.5);
                int eff = blessed ? 2672 : 5884;
                if (await CastAction(runner, bossId, boss, eff, aim, 1.1, now, "[Kraityn] Reave") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, aim, BuffActiveArt, now + 0.9, scale: 1.1f);
                    hits.Add(new(now + 0.55, bossId, boss, aim, 15,
                        HybridAttack(boss, DamageType.Lightning, 0.85, 0.55, 15), gen));
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: blessed ? 1.15 : 1.0, meleeEffect: 319);
        }

        async Task SpawnPossessionWave(Player runner, SpawnedMonster boss, string avatar, (int X, int Y) center)
        {
            string[] spirits = [BearSpirit, BoarSpirit, StagSpirit];
            var rng = AreaRng(runner);
            for (int i = 0; i < spirits.Length; i++)
            {
                var spot = spawnerSpots.Count > 0
                    ? spawnerSpots[rng.Next(spawnerSpots.Count)]
                    : RingSpot(runner, center, i * (Math.Tau / 3.0), 18);
                await SpawnAdd(runner, spirits[i], boss.Level, spot, bossId, adds,
                    rarity: MonsterRarity.Magic, ownedCurrent: () => !stopped && !cleaned && Alive(boss));
            }
            await SpawnAvatarBandits(runner, boss, avatar, center, count: 2);
        }

        async Task SpawnAvatarBandits(Player runner, SpawnedMonster boss, string avatar, (int X, int Y) center, int count)
        {
            (string archer, string melee) = avatar switch
            {
                Greust => (GreustArcher, GreustBandit),
                Oak => (OakArcher, OakBandit),
                Alira => (AliraArcher, AliraBandit),
                _ => (KraitynArcher, KraitynBandit),
            };
            var rng = AreaRng(runner);
            for (int i = 0; i < count; i++)
            {
                var spot = spawnerSpots.Count > 0
                    ? spawnerSpots[rng.Next(spawnerSpots.Count)]
                    : RingSpot(runner, center, rng.NextDouble() * Math.Tau, 18);
                await SpawnAdd(runner, (i & 1) == 0 ? archer : melee, boss.Level, spot, bossId, adds,
                    ownedCurrent: () => !stopped && !cleaned && Alive(boss));
            }
        }

        async Task StepDesecrateGround(Player runner, SpawnedMonster boss, double now)
        {
            if (desecrates.Count == 0)
            {
                ResetExposures(runner);
                return;
            }
            var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (25, 35);
            double dps = (min + max) * 0.4;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = DesecrateExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                    desecrates.Any(z => Distance(at, z.At) <= z.Radius)
                        ? DamagePacket.Of(DamageType.Chaos, dps, dps)
                        : DamagePacket.Empty,
                    () => !stopped && Alive(boss));
            }
        }

        static void ResetExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (DesecrateExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task Cleanup(Player runner, SpawnedMonster boss)
        {
            cleaned = true;
            SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            boss.RemoveTemporaryStats(BlessingStatsKey);
            boss.RebuildStats();
            runner.Area.Buffs.Clear(bossId, RalakeshBuffRow, bossId);
            hits.Clear();
            desecrates.Clear();
            ResetExposures(runner);
            await RetireMonsters(runner, adds);
            adds.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Ralakesh avatar defeated; spirits, bandits, and hazards cleared");
        }
    }
}

using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 4 — The Harvest (<c>1_4_6_3</c>) Malachai encounter controller:
/// <list type="bullet">
/// <item><b>Phase 1 (Core entrance, <c>boss_1.arm</c>)</b>: Malachai starts shielded (<c>BaseCannotBeDamaged</c> +
/// <c>malachai_invulnerable</c> buff 435) while corrupted Piety (<c>PietyMalachaiConverted</c>) fights the player.
/// Defeating corrupted Piety cleanses her into friendly <c>PietyMalachaiAlly</c>, stripping Malachai's shield with
/// her beam (<c>piety_ally_beam</c> buff 436). At 70% HP, <c>Act4.cs</c>'s <c>On.Threshold</c> + <c>On.Forms</c>
/// retreats Malachai into the Black Core and spawns <c>MalachaiBoss2</c>.</item>
/// <item><b>Phase 2 (Black Core, <c>boss_2.arm</c>)</b>: Three-Heart invulnerability cycle at 75% / 50% / 25% HP
/// activating <c>BeastHeart1</c>, <c>BeastHeart2</c>, and <c>BeastHeart3</c> in order; destroying the active Heart
/// drops Malachai's invulnerability and advances the fight, alongside Malachai's rune circle traps, tri-bomb
/// telegraphs, Flayed adds, and teleport slams.</item>
/// </list>
/// </summary>
public static class MalachaiEncounter
{
    public const string Area = "1_4_6_3";
    public const string MalachaiP1 = "Metadata/Monsters/Malachai/MalachaiBoss";
    public const string MalachaiP2 = "Metadata/Monsters/Malachai/MalachaiBoss2";
    public const string PietyCorrupted = "Metadata/Monsters/Axis/PietyMalachaiConverted";
    public const string PietyAlly = "Metadata/Monsters/Axis/PietyMalachaiAlly";
    public static readonly string[] BeastHearts =
    [
        "Metadata/Monsters/Malachai/BeastHeart1",
        "Metadata/Monsters/Malachai/BeastHeart2",
        "Metadata/Monsters/Malachai/BeastHeart3",
    ];
    public const string BloodWorm = "Metadata/Monsters/BloodWorm/BloodWorm";
    public const string FlayedMale = "Metadata/Monsters/Flayed/FlayedMale";
    public const string FlayedFemale = "Metadata/Monsters/Flayed/FlayedFemale";

    const ushort MalachaiInvulnBuff = 435; // malachai_invulnerable
    const ushort PietyAllyBeamBuff = 436;  // piety_ally_beam

    const string RuneTrapArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/rune_circle/trap.ao";
    const string RuneExplosionArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/rune_circle/explosion.ao";
    const string TriBombChargeArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/tribomb/tribombchargeup_attachhere.ao";
    const string TriBombExplosionArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/tribomb/tribombexplosion.ao";
    const string GroundSlamArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/ground_slam/groundslam.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            bool hasMalachai = player.Area.Monsters.Values.Any(m => m.Variety?.Id is MalachaiP1 or MalachaiP2);
            if (!hasMalachai) return;
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

    public static Task Leaving(Player player) => Task.CompletedTask;

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
        }
    }

    sealed record RuneTrap(uint EffectId, (int X, int Y) At, double ArmedAt, double ExpiresAt, bool Triggered);
    sealed record DelayedBurst(double At, (int X, int Y) Spot, double Radius, string BurstAo, DamageType Element, double Scale, uint SourceId, SpawnedMonster Source);

    sealed class Fight(uint seed)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<RuneTrap> runes = [];
        readonly List<DelayedBurst> bursts = [];
        readonly HashSet<uint> p1Adds = [];
        readonly HashSet<uint> p2Adds = [];
        readonly uint[] heartIds = new uint[3];

        bool p1Initialized, p1PietyCleansed, p1Cleaned;
        uint p1BossId, p1PietyCorruptedId, p1PietyAllyId;
        double nextRuneAt, nextTriBombAt, nextTeleportSlamAt, nextAddAt;

        bool p2Initialized, p2Cleaned;
        uint p2BossId;
        int activeHeartIndex = -1;
        bool stopped;

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepRunesAndBursts(runner, now);

            var p1Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == MalachaiP1);
            if (p1Entry.Value is { } p1 && Alive(p1))
                await StepPhase1(runner, p1Entry.Key, p1, now);
            else if (p1Initialized && !p1Cleaned)
                await CleanupPhase1(runner);

            var p2Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == MalachaiP2);
            if (p2Entry.Value is { } p2)
            {
                if (Alive(p2))
                    await StepPhase2(runner, p2Entry.Key, p2, now);
                else if (!p2Cleaned)
                    await CleanupPhase2(runner);
            }
        }

        async Task StepPhase1(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            p1BossId = bossId;
            boss.NoRewards = true;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 120, awakeId: bossId);
            if (target is null) return;

            if (!p1Initialized)
            {
                p1Initialized = true;
                nextRuneAt = now + 5;
                nextTriBombAt = now + 9;
                nextTeleportSlamAt = now + 13;
                nextAddAt = now + 11;
                SetInvulnerable(runner, bossId, boss, true, MalachaiInvulnBuff);

                var pietySpot = PlacementsOf(Area, seed, PietyAlly).FirstOrDefault() is { } p
                    ? (p.X, p.Y)
                    : RingSpot(runner, pos, 0.5, 16);
                p1PietyCorruptedId = await SpawnAdd(runner, PietyCorrupted, boss.Level, pietySpot, bossId, p1Adds,
                    rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !p1Cleaned);
                Console.WriteLine($"[gateway] Malachai P1: corrupted Piety 0x{p1PietyCorruptedId:x} shields Malachai 0x{bossId:x}");
            }

            if (!p1PietyCleansed)
            {
                bool pietyDefeated = p1PietyCorruptedId == 0
                    || !runner.Area.Monsters.TryGetValue(p1PietyCorruptedId, out var cp)
                    || !Alive(cp)
                    || (ulong)cp.Life * 100 <= (ulong)Math.Max(1u, cp.MaxLife) * 20;
                if (pietyDefeated)
                {
                    p1PietyCleansed = true;
                    var allySpot = p1PietyCorruptedId != 0 && runner.Area.Monsters.TryGetValue(p1PietyCorruptedId, out var oldP)
                        ? oldP.Walk.Position(now) : RingSpot(runner, pos, 1.0, 14);
                    if (p1PietyCorruptedId != 0)
                        await RetireMonsters(runner, [p1PietyCorruptedId]);
                    p1PietyCorruptedId = 0;

                    p1PietyAllyId = await SpawnAdd(runner, PietyAlly, boss.Level, allySpot, bossId, p1Adds,
                        friendly: true, rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !p1Cleaned);
                    if (p1PietyAllyId != 0 && runner.Area.Monsters.TryGetValue(p1PietyAllyId, out var ally))
                    {
                        SetInvulnerable(runner, p1PietyAllyId, ally, true);
                        runner.Area.Buffs.Set(p1PietyAllyId, new(PietyAllyBeamBuff, p1PietyAllyId));
                    }
                    SetInvulnerable(runner, bossId, boss, false, MalachaiInvulnBuff);
                    Console.WriteLine($"[gateway] Malachai P1: Piety cleansed -> ally 0x{p1PietyAllyId:x} strips Malachai's shield");
                }
            }

            if (now >= nextRuneAt)
            {
                nextRuneAt = now + 9;
                await CastRuneCluster(runner, bossId, boss, target.Walk.Position(now), count: 3, now);
            }
            if (now >= nextTriBombAt)
            {
                nextTriBombAt = now + 11;
                await CastTriBomb(runner, bossId, boss, target.Walk.Position(now), now);
            }
            if (p1PietyCleansed && now >= nextTeleportSlamAt)
            {
                nextTeleportSlamAt = now + 13;
                await CastTeleportSlam(runner, bossId, boss, target.Walk.Position(now), now);
            }
            if (now >= nextAddAt)
            {
                nextAddAt = now + 14;
                int livingWorms = p1Adds.Count(id => id != p1PietyCorruptedId && id != p1PietyAllyId
                    && runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (livingWorms < 5)
                {
                    var spawners = PlacementsOf(Area, seed, "Metadata/Monsters/BloodWorm/BloodWormSpawnerMalachai");
                    var rng = AreaRng(runner);
                    var spot = spawners.Count > 0
                        ? (spawners[rng.Next(spawners.Count)].X, spawners[rng.Next(spawners.Count)].Y)
                        : RingSpot(runner, pos, rng.NextDouble() * Math.Tau, 18);
                    await SpawnAdd(runner, BloodWorm, boss.Level, spot, bossId, p1Adds,
                        ownedCurrent: () => !stopped && !p1Cleaned);
                }
            }
        }

        async Task CleanupPhase1(Player runner)
        {
            p1Cleaned = true;
            if (p1BossId != 0 && runner.Area.Monsters.TryGetValue(p1BossId, out var b))
                SetInvulnerable(runner, p1BossId, b, false, MalachaiInvulnBuff);
            if (p1PietyAllyId != 0)
                runner.Area.Buffs.Clear(p1PietyAllyId, PietyAllyBeamBuff, p1PietyAllyId);
            await RetireMonsters(runner, p1Adds);
            p1Adds.Clear();
            p1PietyCorruptedId = 0;
            p1PietyAllyId = 0;
            await ClearRunes(runner);
            bursts.RemoveAll(b => b.SourceId == p1BossId);
            Console.WriteLine("[gateway] Malachai P1: retreated into the Black Core");
        }

        async Task StepPhase2(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            p2BossId = bossId;
            boss.LifePhases ??= new([75, 50, 25]);
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: bossId);

            if (!p2Initialized)
            {
                p2Initialized = true;
                nextRuneAt = now + 4.5;
                nextTriBombAt = now + 8;
                nextTeleportSlamAt = now + 11;
                nextAddAt = now + 10;
                await SpawnThreeHearts(runner, bossId, boss, pos);
                Console.WriteLine($"[gateway] Malachai P2: Black Core initialized with 3 Beast Hearts for 0x{bossId:x}");
            }

            if (target is null) return;

            var phases = boss.LifePhases;
            if (!phases.Active && phases.Completed < 3 && phases.Begin(boss.Life, boss.MaxLife))
            {
                activeHeartIndex = phases.Completed;
                SetInvulnerable(runner, bossId, boss, true, MalachaiInvulnBuff);
                await ActivateHeart(runner, bossId, boss, activeHeartIndex, pos, now);
            }

            if (phases.Active && activeHeartIndex is >= 0 and < 3)
            {
                uint hid = heartIds[activeHeartIndex];
                bool heartDead = hid == 0 || !runner.Area.Monsters.TryGetValue(hid, out var hm) || !Alive(hm);
                if (heartDead)
                {
                    if (hid != 0 && runner.Area.Monsters.TryGetValue(hid, out var deadHeart))
                        await SetMonsterTransition(runner, hid, deadHeart, 3, $"[Malachai] BeastHeart{activeHeartIndex + 1} destroyed");
                    activeHeartIndex = -1;
                    SetInvulnerable(runner, bossId, boss, false, MalachaiInvulnBuff);
                    phases.Finish();
                    Console.WriteLine($"[gateway] Malachai P2: Heart {phases.Completed}/3 destroyed; Malachai vulnerable");
                }
            }

            // Malachai continues casting traps/tri-bombs/slams throughout P2 (even while a Heart is active, matching retail pressure).
            if (now >= nextRuneAt)
            {
                nextRuneAt = now + 7.5;
                await CastRuneCluster(runner, bossId, boss, target.Walk.Position(now), count: 4, now);
            }
            if (now >= nextTriBombAt)
            {
                nextTriBombAt = now + 10;
                await CastTriBomb(runner, bossId, boss, target.Walk.Position(now), now);
            }
            if (!phases.Active && now >= nextTeleportSlamAt)
            {
                nextTeleportSlamAt = now + 12;
                await CastTeleportSlam(runner, bossId, boss, target.Walk.Position(now), now);
            }
        }

        async Task SpawnThreeHearts(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) center)
        {
            for (int i = 0; i < 3; i++)
            {
                string heartVariety = BeastHearts[i];
                var placed = PlacementsOf(Area, seed, heartVariety).FirstOrDefault();
                var spot = placed is not null ? (placed.X, placed.Y) : RingSpot(runner, center, i * (Math.Tau / 3.0), 32);
                uint hid = await SpawnAdd(runner, heartVariety, boss.Level, spot, bossId,
                    entranceGrace: true, rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !p2Cleaned);
                heartIds[i] = hid;
                if (hid != 0 && runner.Area.Monsters.TryGetValue(hid, out var hm))
                {
                    SetInvulnerable(runner, hid, hm, true, MalachaiInvulnBuff);
                    await SetMonsterTransition(runner, hid, hm, 1);
                }
            }
        }

        async Task ActivateHeart(Player runner, uint bossId, SpawnedMonster boss, int index, (int X, int Y) center, double now)
        {
            uint hid = heartIds[index];
            if (hid != 0 && runner.Area.Monsters.TryGetValue(hid, out var hm) && Alive(hm))
            {
                hm.EntranceGrace = false;
                SetInvulnerable(runner, hid, hm, false, MalachaiInvulnBuff);
                await SetMonsterTransition(runner, hid, hm, 2, $"[Malachai] BeastHeart{index + 1} exposed");
                Console.WriteLine($"[gateway] Malachai P2: Heart {index + 1}/3 (0x{hid:x}) activated; Malachai shielded");
            }
            // Spawn 2 Flayed adds from room spawners when a heart opens.
            var spawners = PlacementsOf(Area, seed, "Metadata/Monsters/Flayed/FlayedSpawnerMalachai");
            var rng = AreaRng(runner);
            int living = p2Adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            for (int i = 0; i < 2 && living + i < 6; i++)
            {
                var spot = spawners.Count > 0
                    ? (spawners[rng.Next(spawners.Count)].X, spawners[rng.Next(spawners.Count)].Y)
                    : RingSpot(runner, center, rng.NextDouble() * Math.Tau, 24);
                string addType = (i & 1) == 0 ? FlayedMale : FlayedFemale;
                await SpawnAdd(runner, addType, boss.Level, spot, bossId, p2Adds,
                    ownedCurrent: () => !stopped && !p2Cleaned);
            }
        }

        async Task CastRuneCluster(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) aim, int count, double now)
        {
            if (runes.Count >= 12) return;
            var rng = AreaRng(runner);
            for (int i = 0; i < count; i++)
            {
                double angle = rng.NextDouble() * Math.Tau;
                double dist = 4 + rng.NextDouble() * 16;
                var spot = RingSpot(runner, aim, angle, dist);
                if (runes.Any(r => Distance(r.At, spot) < 4)) continue;
                uint fx = await SpawnTimedEffect(runner, effects, spot, RuneTrapArt, now + 9.0, scale: 1.0f);
                runes.Add(new(fx, spot, now + 0.7, now + 8.5, false));
            }
        }

        async Task CastTriBomb(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) aim, double now)
        {
            var rng = AreaRng(runner);
            double baseAngle = rng.NextDouble() * Math.Tau;
            for (int i = 0; i < 3; i++)
            {
                var spot = RingSpot(runner, aim, baseAngle + i * (Math.Tau / 3.0), 10);
                await SpawnTimedEffect(runner, effects, spot, TriBombChargeArt, now + 2.0, scale: 1.1f);
                bursts.Add(new(now + 1.9, spot, 13, TriBombExplosionArt, DamageType.Fire, 1.45, bossId, boss));
            }
        }

        async Task CastTeleportSlam(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) aim, double now)
        {
            var dest = WalkableNear(runner, aim);
            boss.Walk.Teleport(dest.X, dest.Y, now);
            await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, dest.X, dest.Y), null);
            await SpawnTimedEffect(runner, effects, dest, GroundSlamArt, now + 2.2, scale: 1.2f);
            bursts.Add(new(now + 1.4, dest, 15, GroundSlamArt, DamageType.Physical, 1.75, bossId, boss));
        }

        async Task StepRunesAndBursts(Player runner, double now)
        {
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            for (int i = runes.Count - 1; i >= 0; i--)
            {
                var r = runes[i];
                bool expired = now >= r.ExpiresAt;
                bool stepped = now >= r.ArmedAt && players.Any(p => Distance(p.Walk.Position(now), r.At) <= 6);
                if (!expired && !stepped) continue;
                runes.RemoveAt(i);
                effects.RemoveAll(e => e.Id == r.EffectId);
                await SendWorldRemoval(runner, r.EffectId, null);
                if (stepped)
                {
                    await SpawnTimedEffect(runner, effects, r.At, RuneExplosionArt, now + 1.1, scale: 1.3f);
                    uint srcId = p2BossId != 0 && runner.Area.Monsters.ContainsKey(p2BossId) ? p2BossId : p1BossId;
                    if (srcId != 0 && runner.Area.Monsters.TryGetValue(srcId, out var src))
                    {
                        var atk = ElementalSpellAttack(src, DamageType.Fire, 1.25, radius: 11);
                        foreach (var p in players)
                            await ArenaHazards.Hit(p, srcId, src, atk, at => Distance(at, r.At) <= 11, () => !stopped);
                    }
                }
            }

            for (int i = bursts.Count - 1; i >= 0; i--)
            {
                var b = bursts[i];
                if (now < b.At) continue;
                bursts.RemoveAt(i);
                if (!Alive(b.Source)) continue;
                await SpawnTimedEffect(runner, effects, b.Spot, b.BurstAo, now + 1.2, scale: 1.2f);
                var atk = b.Element == DamageType.Physical
                    ? PhysicalAttack(b.Source, b.Scale, b.Radius)
                    : ElementalSpellAttack(b.Source, b.Element, b.Scale, b.Radius);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, b.SourceId, b.Source, atk, at => Distance(at, b.Spot) <= b.Radius, () => !stopped);
            }
        }

        async Task ClearRunes(Player runner)
        {
            foreach (var r in runes.ToArray())
            {
                effects.RemoveAll(e => e.Id == r.EffectId);
                await SendWorldRemoval(runner, r.EffectId, null);
            }
            runes.Clear();
        }

        async Task CleanupPhase2(Player runner)
        {
            p2Cleaned = true;
            if (p2BossId != 0 && runner.Area.Monsters.TryGetValue(p2BossId, out var b))
                SetInvulnerable(runner, p2BossId, b, false, MalachaiInvulnBuff);
            await RetireMonsters(runner, heartIds.Where(id => id != 0));
            Array.Clear(heartIds);
            await RetireMonsters(runner, p2Adds);
            p2Adds.Clear();
            await ClearRunes(runner);
            bursts.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Malachai P2: defeated; Black Core hearts and hazards cleared");
        }
    }
}

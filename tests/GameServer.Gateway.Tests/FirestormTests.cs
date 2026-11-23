using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class FirestormTests
{
    [Fact]
    public async Task Cwdt_pays_once_and_firestorm_has_a_larger_first_hit_followed_by_timed_impacts()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Player;
        conn.Clock.Restart();
        var clock = new HitClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            conn.Walk.Teleport(100, 100, 0);
            ushort skill = Assert.Single(fixture.Equip(("Firestorm", 1), ("Cast when Damage Taken Support", 1)));
            var gem = Game.SupportedSkill(conn, skill);
            Assert.NotNull(Game.GemSkillInstanceDisabled(conn, gem.Effect, skill));
            string? effectId = Game.SkillsFor(conn).EffectId(gem.Effect);
            Assert.True(Game.TriggerPayloadAvailable(conn, gem),
                $"Executor unavailable for {effectId}, plan {SkillHitPlan.ForSkill(Game.SkillsFor(conn), gem.Effect, gem.Level).CanHit}.");
            Assert.Null(Game.GemSkillInstanceDisabled(conn, gem.Effect, skill, triggered: true));
            uint mana = conn.PlayerMana;
            int threshold = gem.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
            var targets = new List<SpawnedMonster>();
            for (int x = 80; x <= 120; x += 10)
            for (int y = 80; y <= 120; y += 10)
            {
                var target = new SpawnedMonster("storm target", new(x, y, 0), 0, 0, 10000);
                conn.Area.Monsters[(uint)(200 + targets.Count)] = target;
                conn.Area.Zones.Grid.Set(new((uint)(200 + targets.Count)), (x, y));
                targets.Add(target);
            }
            long TotalLife() => targets.Sum(target => (long)target.Life);
            long intact = TotalLife();
            async Task Advance(double seconds)
            {
                clock.At = seconds;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(seconds));
                var fence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Session.Enqueue(() => { fence.SetResult(); return Task.CompletedTask; });
                await fence.Task;
            }

            Game.RecordDamageTakenTriggers(conn, threshold - 1, 0);
            Game.ReleaseDamageTakenTriggers(conn, fixture.Session);
            Assert.Equal(mana, conn.PlayerMana);
            Game.RecordDamageTakenTriggers(conn, 1, 0);
            Game.ReleaseDamageTakenTriggers(conn, fixture.Session);
            uint afterPayment = conn.PlayerMana;
            Assert.True(afterPayment < mana, $"No skill payment for {effectId}. Mana {mana}, after {afterPayment}, in town {Game.InTown(conn)}, rules {Game.CurrentGameRules}, threshold {threshold}, rate {gem.Stats.GetValueOrDefault(Stat.CastWhenDamageTakenTriggerThresholdPlusPct)}.");
            await Advance(.149);
            Assert.Equal(intact, TotalLife());
            await Advance(.15);
            long afterFirst = TotalLife();
            Assert.True(afterFirst < intact);
            await Advance(.30);
            Assert.True(TotalLife() < afterFirst);
            await Advance(1.4);
            long afterStorm = TotalLife();
            await Advance(2);
            Assert.Equal(afterStorm, TotalLife());
            Assert.Equal(afterPayment, conn.PlayerMana);

            ushort manual = Assert.Single(fixture.Equip(("Firestorm", 1)));
            Assert.Null(Game.GemSkillInstanceDisabled(conn, Game.SupportedSkill(conn, manual).Effect, manual));
            conn.ProjectileRuntimeState = fixture.Session;
            Assert.True(await Game.CastGemSkill(conn, manual, (100, 100), (100, 100)));
            await Advance(3.5);
            Assert.True(TotalLife() < afterStorm);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Map_hexes_do_not_double_apply_resistances_or_vulnerability_and_essence_drain_loss_kills_cleanly()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Player;
        conn.Clock.Restart();
        var clock = new HitClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var basePools = Game.PoolsFor(conn, conn.Pools.Level);
            var beltType = Program.Items.Find("Chain Belt")!;
            var selfCurseMod = Program.Items.Mods.ById("UniqueSelfCurseVulnerabilityLevel20")!;
            var belt = ItemCatalog.Create(beltType) with
            {
                Rarity = ItemRarity.Unique,
                Mods = [new ItemMod(ModSection.Explicit, selfCurseMod.Hash, [20])],
            };
            Game.ItemsOf(conn).Put(PlayerInventories.Belt, new(2, 0, 0, belt));
            var selfOnly = Game.FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            int selfPhysTaken = selfOnly[Stat.PhysicalDamageTakenPlusPct];
            int selfBleedChance = selfOnly[Stat.ReceiveBleedingChancePctWhenHitByAttack];
            Assert.True(selfPhysTaken > 0 && selfBleedChance > 0);
            Game.ItemsOf(conn).Remove(PlayerInventories.Belt, 2);

            var hexMods = new MapMods(new Dictionary<uint, int>
            {
                [Stat.MapPlayerHasLevelXElementalWeakness] = 15,
                [Stat.MapPlayerHasLevelXVulnerability] = 15,
            });
            conn.Instance.OpenedWith(new("HexOwner", new(conn.Layout.Hash, "HexTest", "HexTest", 1) { Level = 70 }, hexMods));
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            Assert.True(conn.Pools.FireResistance < basePools.FireResistance);
            Assert.True(conn.Pools.ColdResistance < basePools.ColdResistance);
            Assert.True(conn.Pools.LightningResistance < basePools.LightningResistance);
            var wire = Game.ClientStats(conn, Game.FinalPlayerStats(conn).ToList()).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal(0, wire.GetValueOrDefault(Stat.BaseResistAllElementsPct));
            Assert.Equal(conn.Pools.FireResistance, wire[Stat.BaseFireDamageResistancePct]);
            Assert.Equal(1, wire.GetValueOrDefault(Stat.YouAreCursedWithVulnerability));
            Assert.Equal(1, wire.GetValueOrDefault(Stat.CursedByVulnerability));
            int mapPhysTaken = wire[Stat.PhysicalDamageTakenPlusPct];
            int mapBleedChance = wire[Stat.ReceiveBleedingChancePctWhenHitByAttack];
            Assert.True(mapPhysTaken > 0 && mapBleedChance > 0);

            Game.ItemsOf(conn).Put(PlayerInventories.Belt, new(2, 0, 0, belt));
            var combined = Game.FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal(1, combined.GetValueOrDefault(Stat.CursedByVulnerability));
            Assert.Equal(1, combined.GetValueOrDefault(Stat.YouAreCursedWithVulnerability));
            Assert.Equal(Math.Max(mapPhysTaken, selfPhysTaken), combined[Stat.PhysicalDamageTakenPlusPct]);
            Assert.Equal(Math.Max(mapBleedChance, selfBleedChance), combined[Stat.ReceiveBleedingChancePctWhenHitByAttack]);

            conn.PlayerLife = 10;
            conn.EnergyShield.SetMaximum(50, refill: true);
            conn.ChannelSkill = 42;
            conn.ProjectileRuntimeState = fixture.Session;
            var victim = new SpawnedMonster("desperation target", new(110, 100, 0), 0, 0, 10000);
            conn.Area.Monsters[300] = victim;
            double started = Game.ProjectileWorldTime(conn);
            victim.EssenceDrain.Apply(started, new(500, 5, 0, 0, conn) { LossPermyriad = 10_000 });
            clock.At = 0.2;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(0.2));
            await Game.TickEssenceDrains(conn);
            Assert.Equal(0u, conn.PlayerLife);
            Assert.Equal(0u, conn.EnergyShield.Current);
            Assert.Equal(0, conn.ChannelSkill);
            Assert.True(conn.DeathNotificationEpoch > 0 && conn.DeathNotificationEpoch == conn.IncomingHitEpoch);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class HitClock : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
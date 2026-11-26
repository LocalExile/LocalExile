using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WarcryExertionTests
{
    [Fact]
    public async Task Seismic_Cry_extends_six_slam_waves_after_its_armour_buff_expires()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 168, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        fixture.Equip(("Seismic Cry", 1), ("Ground Slam", 1), ("Heavy Strike", 1));
        var cry = Game.GemSkillEffects(player).Single(g => g.Name == "Seismic Cry");
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var strike = Game.GemSkillEffects(player).Single(g => g.Name == "Heavy Strike");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(6, own[Stat.SkillEmpowersNextXMeleeAttacks]);
        Assert.Equal(50, own[Stat.SeismicCryBaseSlamSkillAreaPlusPctFinal]);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player)) stats[Stat.GlobalAlwaysHit] = 1;
            return stats;
        });
        var near = new SpawnedMonster("inside either wave", new(115, 100, 0), 0, 0, 1_000_000);
        var far = new SpawnedMonster("outside the regular wave", new(167, 100, 0), 0, 0, 1_000_000);
        player.Area.Monsters.Add(901, near);
        player.Area.Monsters.Add(902, far);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task Slam(bool reachesFar)
            {
                uint nearLife = near.Life, farLife = far.Life;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, slam.Id, origin, aim));
                Assert.True(near.Life < nearLife);
                Assert.Equal(reachesFar, far.Life < farLife);
            }

            await Slam(false);
            var playerStats = new Dictionary<uint, int>();
            Game.ApplyWarcryBuffLocked(player, "SeismicCry", own, playerStats, origin, 60, clock.At, 2.5, 1);
            Game.GrantSeismicCryExertsLocked(player, own, playerStats);
            Assert.True(player.Area.Buffs.Has(player.Id, 1544, player.Id));
            clock.At += 10;
            Game.ExpireWarcryBuffs(player);
            Assert.False(player.Area.Buffs.Has(player.Id, 1544, player.Id));

            player.NextAttackAt = 0;
            uint previousLife = near.Life;
            Assert.True(await Game.CastGemSkill(player, strike.Id, origin, (115, 100), 901));
            Assert.True(near.Life < previousLife);
            for (int i = 0; i < 6; i++) await Slam(true);
            await Slam(false);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public void War_bringer_doubles_the_native_count_and_each_eligible_attack_spends_one()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var native = IntimidatingCryStats();
        Game.ApplyIntimidatingCryLocked(player, native, new Dictionary<uint, int>(), 1);
        Assert.Equal(2, Game.IntimidatingCryExerts(player));

        Game.ApplyIntimidatingCryLocked(player, native, new Dictionary<uint, int> { [Stat.WarcriesExertTwiceAsManyAttacks] = 1 }, 1);
        Assert.Equal(4, Game.IntimidatingCryExerts(player));
        Assert.False(Game.ConsumeIntimidatingCryExertLocked(player, eligible: false));
        for (int remaining = 3; remaining >= 0; remaining--)
        {
            Assert.True(Game.ConsumeIntimidatingCryExertLocked(player, eligible: true));
            Assert.Equal(remaining, Game.IntimidatingCryExerts(player));
        }
        Assert.False(Game.ConsumeIntimidatingCryExertLocked(player, eligible: true));
    }

    [Theory]
    [InlineData("Molten Strike", false)]
    [InlineData("Shield Charge", false)]
    [InlineData("Shield Charge", true)]
    public async Task Paid_attacks_apply_both_multipliers_only_when_eligible(string attack, bool blockTravelExertion)
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        var clock = new AttackClock();
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                if (blockTravelExertion) values[Stat.TravelSkillsCannotBeExerted] = 1;
            }
            return values;
        });
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) => ReferenceEquals(owner, player)
            ? DamagePacket.Of(DamageType.Physical, 1000, 1000) : rolled);
        try
        {
            if (attack == "Shield Charge")
            {
                var items = Game.ItemsOf(player);
                items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
                    _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
                items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
            }
            ushort skill = attack == "Molten Strike"
                ? Assert.Single(f.Equip((attack, 1), ("Overexertion Support", 1), ("Momentum Support", 1)))
                : Assert.Single(f.Equip((attack, 1), ("Overexertion Support", 1)));
            var native = Game.SupportedSkill(player, skill).Stats;
            Assert.Equal(7, native[Stat.SupportOverexertionDamagePlusPctFinalPerWarcryExertingAction]);
            Assert.Equal(10, native[Stat.SupportOverexertionDamagePlusPctFinalIfExerted]);
            uint normal = await Hit(901);
            Assert.Equal(1000u, normal);

            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            Assert.Equal(2, Game.IntimidatingCryExerts(player));
            Assert.Equal(blockTravelExertion ? normal : 1177u, await Hit(902));
            Assert.Equal(blockTravelExertion ? 2 : 1, Game.IntimidatingCryExerts(player));
            if (attack == "Molten Strike")
                Assert.Equal(native.GetValueOrDefault(Stat.AttackSpeedPlusPct) + 30,
                    Game.SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct));

            async Task<uint> Hit(uint id)
            {
                var target = new SpawnedMonster("exerted attack target", new(104, 100, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, target);
                player.NextAttackAt = 0;
                // At zero travel distance Shield Charge lands one hit and has no distance bonus.
                var aim = attack == "Shield Charge" ? (100, 100) : (104, 100);
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), aim, id));
                while (target.Life == target.MaxLife && clock.At < 10)
                {
                    clock.At += .05;
                    f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                }
                Assert.True(target.Life < target.MaxLife);
                player.Area.Monsters.Remove(id);
                clock.At += 2;
                f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                return target.MaxLife - target.Life;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Overexertion_scales_damaging_ailments_on_exerted_attacks()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        var clock = new AttackClock();
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.BleedOnHitWithAttacksPct] = 100;
                values[Stat.AlwaysIgnite] = 1;
                values[Stat.BaseChanceToPoisonOnHitPct] = 100;
                values[Stat.AttackMinimumAddedPhysicalDamage] = 10000;
                values[Stat.AttackMaximumAddedPhysicalDamage] = 10000;
                values[Stat.AttackMinimumAddedFireDamage] = 10000;
                values[Stat.AttackMaximumAddedFireDamage] = 10000;
                values[Stat.NumberOfAdditionalProjectiles] = 1;
            }
            return values;
        });
        try
        {
            async Task<(double Bleed, double Ignite, double Poison)> Measure(ushort skill, (int X, int Y) targetAt, uint id)
            {
                var target = new SpawnedMonster("exerted ailment target", new(targetAt.X, targetAt.Y, 0), 0, 0, 10_000_000);
                player.Area.Monsters.Add(id, target);
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), targetAt, id));
                while (target.Life == target.MaxLife && clock.At < 40)
                {
                    clock.At += .05;
                    f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                }
                Assert.True(target.Life < target.MaxLife);
                double bleed = target.Bleeds.PerSecond(clock.At);
                double ignite = target.Ignites.PerSecond(clock.At);
                double poison = target.Poisons.TotalDps(clock.At);
                player.Area.Monsters.Remove(id);
                clock.At += 2;
                f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.True(bleed > 0 && ignite > 0 && poison > 0);
                return (bleed, ignite, poison);
            }

            static void AssertScaled((double Bleed, double Ignite, double Poison) normal, (double Bleed, double Ignite, double Poison) exerted)
            {
                Assert.InRange(exerted.Bleed / normal.Bleed, 1.17, 1.184);
                Assert.InRange(exerted.Ignite / normal.Ignite, 1.17, 1.184);
                Assert.InRange(exerted.Poison / normal.Poison, 1.17, 1.184);
            }

            // Molten Strike melee (MoltenStrike.cs) + Lightning Strike melee (Combat.cs) and projectile (Projectiles.cs).
            ushort molten = Assert.Single(f.Equip(("Molten Strike", 1), ("Overexertion Support", 1)));
            var moltenNormal = await Measure(molten, (104, 100), 951);
            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            var moltenExerted = await Measure(molten, (104, 100), 952);
            AssertScaled(moltenNormal, moltenExerted);

            ushort lightning = Assert.Single(f.Equip(("Lightning Strike", 1), ("Overexertion Support", 1)));
            Game.ConsumeIntimidatingCryExertLocked(player, eligible: true); // spend remaining exert
            var areaNormal = await Measure(lightning, (104, 100), 953);
            var projNormal = await Measure(lightning, (118, 100), 954);
            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            var areaExerted = await Measure(lightning, (104, 100), 955);
            var projExerted = await Measure(lightning, (118, 100), 956);
            AssertScaled(areaNormal, areaExerted);
            AssertScaled(projNormal, projExerted);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static Dictionary<uint, int> IntimidatingCryStats()
    {
        int effect = Program.Items.FindGem("Intimidating Cry")!.Value.Variant.Effect;
        return Program.Skills.EffectStats(effect, 1, 0).ToDictionary(stat => stat.Item1, stat => stat.Item2);
    }

    sealed class AttackClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}

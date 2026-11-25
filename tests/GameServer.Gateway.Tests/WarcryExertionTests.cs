using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WarcryExertionTests
{
    [Fact]
    public async Task War_bringer_doubles_the_native_count_and_each_eligible_attack_spends_one()
    {
        await MutewindPennantGrantsOnslaughtAtPaidWarcryRelease();
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

    static async Task MutewindPennantGrantsOnslaughtAtPaidWarcryRelease()
    {
        var shield = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Mutewind Pennant", new Random(17)));
        uint hash = Program.Items.Mods.ById("OnslaughtOnUsingWarcryUnique__1")!.Hash;
        Assert.Equal(4000, Assert.Single(Assert.Single(shield.Mods, mod => mod.Hash == hash).Values));
        var control = shield with { Mods = [.. shield.Mods.Where(mod => mod.Hash != hash)] };
        var plain = await Warcry(control);
        var active = await Warcry(shield);
        Console.WriteLine($"Intimidating Cry Onslaught seconds: {plain} -> {active}");
        Assert.Equal(0, plain);
        Assert.Equal(4, active, 6);

        async Task<double> Warcry(Item equipped)
        {
            var clock = new AttackClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            player.Walk.Teleport(100, 100, clock.At);
            var items = Game.ItemsOf(player);
            uint main = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, main, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            items.Add(PlayerInventories.Offhand1, 0, 0, equipped);
            ushort cry = Assert.Single(fixture.Equip(("Intimidating Cry", 1)));
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe()
            {
                if (player.RecentEvents.Happened(StatConditions.UsedWarcry, clock.At)) released.TrySetResult();
            }
            player.RecentEvents.Changed += Observe;
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                Assert.False(Game.KillOnslaughtOf(player).Applied);
                uint mana = player.PlayerMana;
                int effect = Game.SupportedSkill(player, cry).Effect;
                Assert.True(Game.TryBeginSkillCooldownUse(player, cry, effect, Game.CooldownForSkill(player, cry),
                    instant: false, () => { }, out var cooldown));
                Assert.True(await Game.CastGemSkill(player, cry, (100, 100), (100, 100), cooldownUse: cooldown));
                Assert.True(player.PlayerMana < mana);
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using (Game.ProjectileMutation(player, advance: false))
                {
                    Assert.Equal(2, Game.IntimidatingCryExerts(player));
                    double seconds = Math.Max(0, Game.KillOnslaughtOf(player).Until - Game.ProjectileWorldTime(player));
                    clock.At += 4.1;
                    Game.KillOnslaughtPresentation(player);
                    Assert.False(Game.KillOnslaughtOf(player).Applied);
                    return seconds;
                }
            }
            finally
            {
                player.RecentEvents.Changed -= Observe;
                Game.AreaUpdateContext.Value = previous;
            }
        }
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

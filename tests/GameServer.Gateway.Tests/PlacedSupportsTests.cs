using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

public sealed class PlacedSupportsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Locus_keeps_close_mines_armed_while_the_rest_of_the_sequence_fires()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new PlacedClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            const double Until = 20;
            PlacedSkills.Placed Mine(int distance, bool locus)
            {
                uint id = (uint)(700 + distance);
                var own = new Dictionary<uint, int>();
                if (locus) own[Stat.SupportLocusMineCannotDetonateMinesWithinXUnits] = 30;
                var mine = new PlacedSkills.Placed(id, 0, Program.Skills.EffectRow("IceNova"), (100 + distance, 100),
                    true, clock.At, Until, DamagePacket.Of(DamageType.Cold, 100, 100), own, 0, 0, false, 0);
                player.Placed.All.Add(id, mine);
                return mine;
            }

            var near = Mine(29, true);
            Assert.False(Game.CanDetonateMines(player));
            var atBoundary = Mine(30, true);
            var ordinary = Mine(1, false);
            Assert.True(Game.CanDetonateMines(player));
            Game.DetonateMinesLocked(player, clock.At);
            Assert.Equal(double.PositiveInfinity, near.DetonateAt);
            Assert.Equal(2, player.Placed.Chain);
            clock.At += .75;
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Same(near, Assert.Single(player.Placed.All.Values));
            Assert.DoesNotContain(atBoundary.Id, player.Placed.All.Keys);
            Assert.DoesNotContain(ordinary.Id, player.Placed.All.Keys);

            player.Walk.Teleport(90, 100, clock.At);
            Assert.True(Game.CanDetonateMines(player));
            Game.DetonateMinesLocked(player, clock.At);
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Empty(player.Placed.All);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Invention_scales_real_trap_and_mine_hits_by_time_on_the_ground()
    {
        await TinkerskinRecoversShieldWhenAnEnemyTriggersTheTrap();
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new PlacedClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.SpellMinimumAddedColdDamage] =
                    values[Stat.SpellMaximumAddedColdDamage] = 100_000;
            }
            return values;
        });
        try
        {
            foreach (string placement in new[] { "Trap Support", "Blastchain Mine Support" })
            {
                var gems = new List<(string, int)>
                {
                    ("Ice Nova", 1), (placement, 1), ("Invention Support", 1),
                    ("Added Cold Damage Support", 1), ("Summon Skitterbots", 1),
                };
                if (placement == "Trap Support") gems.Add(("Elemental Focus Support", 1));
                fixture.Equip(gems.ToArray());
                var equipped = Game.GemSkillEffects(player);
                ushort skillId = equipped.Single(g => Program.Skills.EffectId(g.Effect) == "IceNova").Id;
                var skitter = equipped.Single(g => Program.Skills.EffectId(g.Effect) == "SummonSkitterbots");
                Assert.Equal(14, Game.SupportedSkill(player, skillId).Stats
                    .GetValueOrDefault(Stat.SupportInventionTrapAndMineDamagePlusPctFinalPerSecondPlacedUpTo100));
                var (fresh, age) = await Contact(0);
                var (aged, laterAge) = await Contact(3);
                double expected = fresh * (1 + Math.Min(100, laterAge * 14) / 100) / (1 + Math.Min(100, age * 14) / 100);
                output.WriteLine($"{placement}: age {age:F3}s {fresh} damage; age {laterAge:F3}s {aged} damage; expected about {expected:F0}.");
                // The spells retain their small native damage range on top of the fixed added damage.
                Assert.InRange(aged, expected * .9995, expected * 1.0005);

                player.Auras[skitter.Id] = new(skitter.Id, skitter.Effect, "SummonSkitterbots", "Summon Skitterbots", 1, 0, 0, 0);
                Game.MarkPlayerStats(player, PlayerStatStep.Auras);
                var (buffed, buffedAge) = await Contact(0);
                player.Auras.Remove(skitter.Id);
                Game.MarkPlayerStats(player, PlayerStatStep.Auras);
                double bonus = Program.Skills.EffectStats(skitter.Effect, 1)
                    .Single(p => p.Item1 == Stat.SkitterbotsTrapMineDamagePlusPctFinal).Item2 / 100d;
                double withSkitterbots = fresh * (1 + bonus) * (1 + buffedAge * .14) / (1 + age * .14);
                output.WriteLine($"{placement}: plain {fresh}, Skitterbots {buffed}, expected {withSkitterbots:F0}.");
                Assert.InRange(buffed, withSkitterbots * .9995, withSkitterbots * 1.0005);

                async Task<(uint Damage, double Age)> Contact(double wait)
                {
                    clock.At += 1;
                    fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                    Assert.True(await Game.CastGemSkill(player, skillId, (100, 100), (112, 100)));
                    var placed = Assert.Single(player.Placed.All.Values);
                    Assert.DoesNotContain(player.Area.Monsters.Values, m => m.Hostile);
                    const uint Target = 750;
                    var monster = new SpawnedMonster("invention target", new(placed.At.X, placed.At.Y, 0), 0, 0, 1_000_000);
                    player.Area.Monsters.Add(Target, monster);
                    clock.At = placed.Armed + .01 + wait;
                    Assert.True(clock.At < placed.Until);
                    if (placed.Mine) Game.DetonateMinesLocked(player, clock.At);
                    await Game.PlayerMinionsTick(player, clock.At);
                    player.Area.Monsters.Remove(Target);
                    Assert.Empty(player.Placed.All);
                    Assert.True(monster.Life < monster.MaxLife);
                    return (monster.MaxLife - monster.Life, clock.At - placed.LandAt);
                }
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static async Task TinkerskinRecoversShieldWhenAnEnemyTriggersTheTrap()
    {
        var clock = new PlacedClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var armour = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Tinkerskin", new Random(17)));
        uint mod = Program.Items.Mods.ById("GainEnergyShieldOnTrapTriggeredUnique__1_")!.Hash;
        Assert.Equal(50, Assert.Single(Assert.Single(armour.Mods, value => value.Hash == mod).Values));
        var plain = armour with { Mods = [.. armour.Mods.Where(value => value.Hash != mod)] };
        var items = Game.ItemsOf(player);
        uint host = items.Add(PlayerInventories.BodyArmour, 0, 0, plain)!.Id;
        ushort skill = Assert.Single(fixture.Equip(("Ice Trap", 18), ("Added Cold Damage Support", 18), ("Increased Area of Effect Support", 18)));
        Assert.Equal(2, Game.SupportedSkill(player, skill).Supports.Count);
        using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player)) values[Stat.BaseMaximumEnergyShield] = 200;
            return values;
        });
        Game.RecomputePoolsAndSpeed(player);
        Assert.True(player.Pools.EnergyShield >= 50);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            uint before = await Trigger(proximity: true);
            items.Change(PlayerInventories.BodyArmour, host, _ => armour);
            Assert.Equal(50, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.GainXEsOnTrapTriggeredByAnEnemy));
            uint after = await Trigger(proximity: true);
            uint expired = await Trigger(proximity: false);
            Console.WriteLine($"Tinkerskin Energy Shield: proximity {before} -> {after}, duration {expired}");
            Assert.Equal((0u, 50u, 0u), (before, after, expired));
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        async Task<uint> Trigger(bool proximity)
        {
            clock.At += 1;
            fixture.Session.AdvanceTo(Math.Max(fixture.Session.Runtime.Now, ProjectileRuntime.Time(clock.At)));
            player.EnergyShield.Empty();
            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            Assert.True(player.PlayerMana < mana);
            var placed = Assert.Single(player.Placed.All.Values);
            Assert.False(placed.Mine);
            const uint id = 900;
            var enemy = new SpawnedMonster("ice trap target", new(placed.At.X, placed.At.Y, 0), 0, 0, 100000);
            if (proximity) player.Area.Monsters.Add(id, enemy);
            clock.At = proximity ? placed.Armed + .01 : placed.Until + .01;
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Empty(player.Placed.All);
            if (proximity)
            {
                Assert.True(enemy.Life < enemy.MaxLife);
                player.Area.Monsters.Remove(id);
            }
            return player.EnergyShield.Current;
        }
    }

    sealed class PlacedClock : IPlayerUpdateSchedule
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

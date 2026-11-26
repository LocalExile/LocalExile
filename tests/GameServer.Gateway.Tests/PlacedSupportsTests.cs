using GameServer.Core.Projectiles;
using GameServer.Core.World;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

public sealed class PlacedSupportsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Shock Nova", "Trap Support", false)]
    [InlineData("Glacial Cascade", "Blastchain Mine Support", false)]
    [InlineData("Dark Bargain of Trarthus", "Trap Support", false)]
    [InlineData("Purifying Flame", "Trap Support", true)]
    public async Task Placed_spells_release_the_native_payload_only_with_a_current_source(
        string spell, string support, bool removeSource)
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
            ushort skill = Assert.Single(fixture.Equip((spell, 1), (support, 1)));
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            var placed = Assert.Single(player.Placed.All.Values);
            int offset = spell == "Glacial Cascade" ? 12 : 0;
            var monster = new SpawnedMonster("placed spell target", new(placed.At.X + offset, placed.At.Y, 0), 0, 0, 10_000);
            player.Area.Monsters.Add(750, monster);
            if (removeSource) fixture.Equip(("Fireball", 1));

            clock.At = placed.Armed + .05;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            if (placed.Mine) Game.DetonateMinesLocked(player, clock.At);
            await Game.PlayerMinionsTick(player, clock.At);
            clock.At += 1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Empty(player.Placed.All);
            Assert.Equal(removeSource, monster.Life == monster.MaxLife);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trarthus_mine_starts_the_ring_at_the_target_only_with_projected_novas(bool projected)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new PlacedClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (owner == player && projected) values[Stat.NovaSpellsCastAtTargetLocation] = 1;
            return values;
        });
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Wave of Conviction of Trarthus", 1), ("Blastchain Mine Support", 1)));
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            var mine = Assert.Single(player.Placed.All.Values);
            var enemy = new SpawnedMonster("ring target", new(mine.At.X + 30, mine.At.Y, 0), 0, 0, 10_000);
            player.Area.Monsters.Add(750, enemy);
            clock.At = mine.Armed + .05;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Game.DetonateMinesLocked(player, clock.At);
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Empty(player.Placed.All);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At + .02));
            Assert.Equal(projected, enemy.Life < enemy.MaxLife);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Purifying_Flame_trap_respects_spell_block_on_monsters_inside_its_impact()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new PlacedClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        player.Frames.World(player);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Purifying Flame", 1), ("Trap Support", 1)));
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            var trap = Assert.Single(player.Placed.All.Values);
            for (uint i = 0; i < 24; i++)
            {
                var enemy = new SpawnedMonster("spell blocker", new(trap.At.X, trap.At.Y, 0), 0, 0, 10_000);
                enemy.SetTemporaryStats("block", new Dictionary<uint, int> { [Stat.BaseSpellBlockPct] = 75 });
                player.Area.Monsters.Add(750 + i, enemy);
            }
            clock.At = trap.Armed + .05;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            await Game.PlayerMinionsTick(player, clock.At);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At + .02));
            Assert.Empty(player.Placed.All);
            int hits = player.Area.Monsters.Values.Count(m => m.Life < m.MaxLife);
            Assert.InRange(hits, 1, 23);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

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

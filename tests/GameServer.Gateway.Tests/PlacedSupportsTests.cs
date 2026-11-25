using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

public sealed class PlacedSupportsTests(ITestOutputHelper output)
{
    static async Task Architects_Hand_applies_cast_speed_to_the_paid_trap_throw_clock()
    {
        var ordinary = await Timing(equipped: false, castSpeed: 100);
        var speed = await Timing(equipped: true, castSpeed: 100);
        var bare = await Timing(equipped: true, castSpeed: 0);
        Assert.Equal(ordinary.Trap, bare.Trap, 3);
        Assert.InRange(speed.Trap, ordinary.Trap / 2 - .035, ordinary.Trap / 2 + .035);
        Assert.True(speed.Trap < ordinary.Trap);
        Assert.Equal(ordinary.Spell, speed.Spell, 3);

        static async Task<(double Trap, double Spell)> Timing(bool equipped, int castSpeed)
        {
            var clock = new PlacedClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            fixture.Equip(("Fire Trap", 1), ("Ice Nova", 1));
            if (equipped)
            {
                var gloves = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Architect's Hand", new Random(8)));
                var mod = Program.Items.Mods.ById("CastSpeedAppliesToTrapSpeedUnique__1")!;
                Game.ItemsOf(player).Add(PlayerInventories.Gloves, 0, 0,
                    gloves with { Mods = [new(ModSection.Explicit, mod.Hash, [1])] });
            }
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(player, owner)) values[Stat.BaseCastSpeedPlusPct] = castSpeed;
                return values;
            });
            var gems = Game.GemSkillEffects(player).ToDictionary(gem => gem.Name);
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                player.Walk.Teleport(100, 100, clock.At);
                double start = clock.At;
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, gems["Fire Trap"].Id, (100, 100), (112, 100)));
                Assert.True(player.PlayerMana < mana);
                var trap = Assert.Single(player.Placed.All.Values);
                double flight = Game.PlacedFlightSeconds(false, Game.CharacterPlayerStats(player).GetValueOrDefault);
                double release = trap.LandAt - flight - start;
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                start = clock.At;
                Assert.True(await Game.CastGemSkill(player, gems["Ice Nova"].Id, (100, 100), (100, 100)));
                return (release, clock.At - start);
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    static async Task Totem_spell_hit_sources_do_not_grant_surge_for_trap_or_mine_projectiles()
    {
        foreach (string support in new[] { "Trap Support", "Blastchain Mine Support" })
        {
            void Equip(Player player)
            {
                var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("That Which Was Taken", new Random(6)));
                var mod = Program.Items.Mods.ById("AnimalCharmArcaneSurgeOnHit")!;
                const byte slot = 1;
                player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
                player.Character.Passives.Add(31703);
                Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0,
                    jewel with { Mods = [new(ModSection.Explicit, mod.Hash, [1])] });
                Assert.Equal(1, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.GainArcaneSurgeOnSpellHitByYouOrYourTotems));
            }
            var hit = await PlacedContact("Fireball", support, true, Equip);
            Assert.True(hit.Damage > 0);
            Assert.False(hit.Surge);
        }
    }

    static async Task<(uint Damage, bool Surge)> PlacedContact(string name, string? support, bool enemy,
        Action<Player>? setup)
    {
        var clock = new PlacedClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var items = Game.ItemsOf(player);
        uint weapon = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        items.Change(PlayerInventories.Weapon1, weapon, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        ushort skill = Assert.Single(support is null ? fixture.Equip((name, 18)) : fixture.Equip((name, 18), (support, 18)));
        setup?.Invoke(player);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.RecomputePoolsAndSpeed(player);
            int unreservedBefore = Game.UnreservedMana(player);
            if (support is not null) player.PlayerMana = (uint)unreservedBefore;
            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            var placed = Assert.Single(player.Placed.All.Values);
            Assert.True(player.PlayerMana < mana || placed.Mine && Game.UnreservedMana(player) < unreservedBefore,
                $"{name} with {support}: mana {mana}→{player.PlayerMana}, unreserved {unreservedBefore}→{Game.UnreservedMana(player)}");
            Assert.False(player.SupportGems.ArcaneSurgeActiveAt(clock.At));
            Assert.Empty(player.FlaskGrounds.Grounds);
            const uint id = 915;
            var at = (X: placed.At.X + 5, Y: placed.At.Y);
            var target = new SpawnedMonster("placed skill witness", new(at.X, at.Y, 0), 0, 0, 100000)
                { ObjectId = id, Hostile = true };
            if (enemy)
            {
                player.Area.Monsters.Add(id, target);
                player.Area.Grid.Set(new(id), at);
            }
            clock.At = enemy || placed.Mine ? placed.Armed + .01 : placed.Until + .01;
            if (placed.Mine) Game.DetonateMinesLocked(player, clock.At);
            await Game.PlayerMinionsTick(player, clock.At);
            clock.At += .50;
            fixture.Session.AdvanceTo(Math.Max(fixture.Session.Runtime.Now, ProjectileRuntime.Time(clock.At)));
            Assert.Empty(player.Placed.All);
            return (target.MaxLife - target.Life, player.SupportGems.ArcaneSurgeActiveAt(clock.At));
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

        await Architects_Hand_applies_cast_speed_to_the_paid_trap_throw_clock();
        await Totem_spell_hit_sources_do_not_grant_surge_for_trap_or_mine_projectiles();
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

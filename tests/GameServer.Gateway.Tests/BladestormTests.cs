using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class BladestormTests
{
    [Theory]
    [InlineData("Blood")]
    [InlineData("Sand")]
    public async Task Storms_persist_after_a_weapon_swap_and_ailments_have_the_correct_scope(string stance)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var weapon = EquipWeapon(fixture, clock.At);
        var inventory = Game.ItemsOf(player);
        player.Stance = stance;
        var peer = new Player(player.Layout, 1, 1)
        {
            Id = player.Id + 1,
            Instance = player.Instance,
            LeagueModule = player.LeagueModule,
            PlayerLife = 1000,
            Character = { Name = "StormObserver", League = "Standard" },
        };
        Assert.Same(player.Area, peer.Area);
        using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.GlobalAlwaysHit] = 1;
            stats[Stat.GlobalCannotCrit] = 1;
            stats[Stat.BlindEffectPlusPct] = 50;
            return stats;
        });
        ushort skill = Assert.Single(fixture.Equip(("Bladestorm", 1)));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (120, 100)));
            Assert.Empty(player.Area.Monsters);

            var late = AddTarget(901);
            inventory.Change(PlayerInventories.Weapon1, weapon,
                _ => ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!));
            Assert.Empty(Game.GemSkillEffects(player));
            Step(1.8);

            Assert.InRange(late.Life, 1u, 999u);
            Assert.Equal(stance == "Blood", late.Bleeds.IgnitedAt(clock.At));
            Assert.Equal(stance == "Sand", Game.Blinded(player, late.ObjectId, clock.At));
            Assert.Equal(stance == "Sand", Game.Blinded(peer, late.ObjectId, clock.At));
            Assert.Equal(stance == "Sand" ? 700 : 1000,
                Game.MonsterBlindRating(peer, late.ObjectId, late, 1000, clock.At));

            Step(1.3);
            Assert.False(Game.Blinded(peer, late.ObjectId, clock.At));
            Assert.Equal(1000, Game.MonsterBlindRating(peer, late.ObjectId, late, 1000, clock.At));
            var afterExpiry = AddTarget(902);
            Step(1.5);
            Assert.Equal(1000u, afterExpiry.Life);
            Assert.False(afterExpiry.Bleeds.IgnitedAt(clock.At));
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        SpawnedMonster AddTarget(uint id)
        {
            var monster = new SpawnedMonster("storm target", new(116, 100, 0), 0, 0, 1000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = monster;
            player.Area.Grid.Set(new(id), (116, 100));
            return monster;
        }

        void Step(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }

    [Theory]
    [InlineData(0, 3, "Bladestorm")]
    [InlineData(20, 4, "Bladestorm")]
    [InlineData(0, 3, "BladestormRoyale")]
    public async Task Bladestorm_quality_changes_the_limit_and_excess_storms_retire_oldest_first(int quality, int limit,
        string effect)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var weapon = EquipWeapon(fixture, clock.At);
        var inventory = Game.ItemsOf(player);
        using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.AttackSpeedPlusPct] = 600;
            stats[Stat.Strength] = 100;
            stats[Stat.Dexterity] = 100;
            return stats;
        });
        var gemType = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("SkillGem" + effect));
        var gem = ItemCatalog.Create(gemType) with { Quality = (byte)quality };
        inventory.Change(PlayerInventories.Weapon1, weapon, item => item with
        {
            Sockets = [new(SocketColour.White, gem)],
            Links = [1],
        });
        var known = Assert.Single(Game.GemSkillEffects(player));
        Assert.Equal(effect, Program.Skills.EffectId(known.Effect));
        ushort skill = known.Id;
        Assert.Equal(limit, Game.SupportedSkill(player, skill).Stats[Stat.BladestormMaximumNumberOfStormsAllowed]);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.RecomputePoolsAndSpeed(player);
            int ordinarySpeed = player.Pools.AttackSpeedPct;
            double timing = Game.SupportedAttackSeconds(player, skill);
            await CastAndFlush();
            Assert.Equal(ordinarySpeed, player.Pools.AttackSpeedPct);
            Assert.True(Game.SupportedAttackSeconds(player, skill) < timing);
            uint first = Assert.Single(player.ProjectileObjects);
            for (int i = 1; i < limit; i++) await CastAndFlush();
            Assert.Equal(limit, player.ProjectileObjects.Count);
            Assert.Contains(first, player.ProjectileObjects);

            await CastAndFlush();
            Assert.Equal(limit, player.ProjectileObjects.Count);
            Assert.DoesNotContain(first, player.ProjectileObjects);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        async Task CastAndFlush()
        {
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (120, 100)));
            clock.At += .22;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Session.Enqueue(() => { sent.SetResult(); return Task.CompletedTask; });
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Bladestorm_projects_stance_stat_scales_sandstorm_area_and_forwards_exertion_to_storms()
    {
        // Native Bladestorm sandstorm AoE modifier (0x14274e883): only applies when affected_by_sandstorm != 0.
        Assert.Equal(1.0, SkillArea.Modifier(k => k == Stat.BladestormAreaOfEffectPlusPctFinalWhileInSandstorm ? 25 : 0, spell: false), 6);
        Assert.Equal(1.25, SkillArea.Modifier(k => k == Stat.BladestormAreaOfEffectPlusPctFinalWhileInSandstorm ? 25
            : k == Stat.AffectedBySandstorm ? 1 : 0, spell: false), 6);

        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        EquipWeapon(fixture, clock.At);
        bool nonExertedZero = false;
        using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.GlobalAlwaysHit] = 1;
            stats[Stat.GlobalCannotCrit] = 1;
            stats[Stat.AttackMinimumAddedPhysicalDamage] = stats[Stat.AttackMaximumAddedPhysicalDamage] = 200;
            if (nonExertedZero) stats[Stat.NonExertedAttacksDealNoDamage] = 1;
            return stats;
        });
        fixture.Equip(("Bladestorm", 1), ("Overexertion Support", 1), ("Rallying Cry", 1), ("Infernal Cry", 1));
        var effects = Game.GemSkillEffects(player);
        ushort skill = effects.Single(g => g.Name == "Bladestorm").Id;
        ushort cry = effects.Single(g => g.Name == "Rallying Cry").Id;
        ushort infernalCry = effects.Single(g => Program.Skills.EffectId(g.Effect) == "InfernalCry").Id;

        // Bladestorm projects in_sand_stance onto its skill stats for the native action (0x141b5ea30).
        player.Stance = StanceCatalog.BloodSelection;
        var bloodSkill = Game.PlayerSkills(player).Single(s => s.Id == skill);
        Assert.NotNull(bloodSkill.Stats);
        Assert.Equal(0, bloodSkill.Stats.Single(s => s.Key == Stat.InSandStance).Value);
        player.Stance = StanceCatalog.SandSelection;
        var sandSkill = Game.PlayerSkills(player).Single(s => s.Id == skill);
        Assert.NotNull(sandSkill.Stats);
        Assert.Equal(1, sandSkill.Stats.Single(s => s.Key == Stat.InSandStance).Value);
        player.Stance = StanceCatalog.BloodSelection;

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            double period = Game.SupportedAttackSeconds(player, skill);
            var initialTarget = AddTarget(910, (112, 100));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            uint plainSpin = initialTarget.MaxLife - initialTarget.Life;
            Assert.True(plainSpin > 0);
            player.Area.Monsters.Remove(910);

            var plainStormTarget = AddTarget(911, (108, 100));
            Step(period + 0.05);
            uint plainPulse = plainStormTarget.MaxLife - plainStormTarget.Life;
            Assert.True(plainPulse > 0);
            player.Area.Monsters.Remove(911);
            Step(4.0);

            // Exert with Rallying Cry (2 allies -> +10% more) + Overexertion Support (+18% more) = ~1.298x on both spin and storm.
            Game.GrantRallyingCryExertsLocked(player, Game.SupportedSkill(player, cry).Stats,
                new Dictionary<uint, int>(), affectedAllies: 2);
            var exertedSpinTarget = AddTarget(912, (112, 100));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            uint exertedSpin = exertedSpinTarget.MaxLife - exertedSpinTarget.Life;
            player.Area.Monsters.Remove(912);

            var exertedStormTarget = AddTarget(913, (108, 100));
            Step(period + 0.05);
            uint exertedPulse = exertedStormTarget.MaxLife - exertedStormTarget.Life;
            Assert.InRange(exertedSpin / (double)plainSpin, 1.28, 1.32);
            Assert.InRange(exertedPulse / (double)plainPulse, 1.28, 1.32);

            // Infernal Cry exertion count must also survive into storm pulses (even under NonExertedAttacksDealNoDamage).
            Step(4.0);
            nonExertedZero = true;
            Game.GrantInfernalCryExertsLocked(player, infernalCry, Game.SupportedSkill(player, infernalCry).Stats,
                new Dictionary<uint, int>());
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (112, 100)));
            var infernalStormTarget = AddTarget(914, (108, 100));
            Step(period + 0.05);
            Assert.True(infernalStormTarget.MaxLife - infernalStormTarget.Life > 0,
                "Expected Infernal Cry-exerted Bladestorm storm pulse to deal damage under NonExertedAttacksDealNoDamage.");
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        SpawnedMonster AddTarget(uint id, (int X, int Y) at)
        {
            var monster = new SpawnedMonster("bladestorm witness", new(at.X, at.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = monster;
            player.Area.Grid.Set(new(id), at);
            return monster;
        }

        void Step(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }

    static uint EquipWeapon(TriggeredSummonTests.Fixture fixture, double now)
    {
        var player = fixture.Player;
        player.Use(new(1, "bladestorm-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, now);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        var inventory = Game.ItemsOf(player);
        var weapon = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        inventory.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        return weapon.Id;
    }
}

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

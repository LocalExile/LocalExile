using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class BoneshatterTests
{
    [Fact]
    public async Task Boneshatter_gains_trauma_and_takes_a_physical_self_hit_after_contact()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Boneshatter", 1)));
            var target = Target(player, 921, 108, 100);
            uint life = player.PlayerLife;
            await Swing(fixture, clock, skill, 921);
            Assert.True(target.Life < target.MaxLife);
            Assert.Equal(life - 6, player.PlayerLife);
            Assert.True(player.Area.Buffs.HasRow(player.Id, 1873));
            await Swing(fixture, clock, skill, 921);
            Assert.Equal(life - 18, player.PlayerLife);
            Advance(fixture, clock, 8);
            Assert.True(player.Area.Buffs.HasRow(player.Id, 1873));
            Advance(fixture, clock, 9);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));

            using var duration = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player)) values[Stat.SkillEffectDurationPlusPct] = 200;
                return values;
            });
            skill = Assert.Single(fixture.Equip(("Boneshatter of Complex Trauma", 1)));
            for (int stack = 1; stack <= 10; stack++)
            {
                life = player.PlayerLife;
                await Swing(fixture, clock, skill, 921);
                Assert.Equal(life - 18 * (uint)stack, player.PlayerLife);
                Assert.Equal(stack < 10, player.Area.Buffs.HasRow(player.Id, 1873));
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Boneshatter_of_carnage_releases_a_pulse_only_after_a_stun()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.AlwaysStunEnemiesThatAreOnFullLife] = 1;
            values[Stat.GlobalCannotCrit] = 1;
            return values;
        });
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Boneshatter of Carnage", 1)));
            var primary = Target(player, 931, 108, 100, suspendStun: true);
            var pulse = Target(player, 932, 108, 117);
            var outside = Target(player, 933, 108, 132);
            uint life = player.PlayerLife;
            await Swing(fixture, clock, skill, 931);
            Assert.True(primary.Life < primary.MaxLife);
            Assert.Equal(pulse.MaxLife, pulse.Life);
            primary = Target(player, 931, 108, 100);
            await Swing(fixture, clock, skill, 931);
            Assert.True(primary.FlinchUntil > 0);
            Assert.True(pulse.Life < pulse.MaxLife);
            Assert.Equal(outside.MaxLife, outside.Life);
            Assert.Equal(life, player.PlayerLife);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static Player Prepare(TriggeredSummonTests.Fixture fixture, Clock clock)
    {
        var player = fixture.Player;
        player.Use(new(1, "boneshatter-test", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        player.Pools = player.Pools with { Accuracy = 10_000 };
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        return player;
    }

    static SpawnedMonster Target(Player player, uint id, int x, int y, bool suspendStun = false)
    {
        var enemy = new SpawnedMonster("boneshatter target", new(x, y, 0), 0, 0, 100_000)
        {
            ObjectId = id, NoRewards = true, CannotBeStunned = suspendStun, HitAnimations = [new(0, .5)],
        };
        player.Area.Monsters[id] = enemy;
        player.Area.Grid.Set(new(id), (x, y));
        return enemy;
    }

    static async Task Swing(TriggeredSummonTests.Fixture fixture, Clock clock, ushort skill, uint id)
    {
        uint mana = fixture.Player.PlayerMana;
        Assert.True(await Game.CastGemSkill(fixture.Player, skill, (100, 100), (108, 100), id));
        Advance(fixture, clock, clock.At + 1);
        Assert.True(fixture.Player.PlayerMana < mana);
    }

    static void Advance(TriggeredSummonTests.Fixture fixture, Clock clock, double now)
    {
        clock.At = now;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(now));
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;

        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}

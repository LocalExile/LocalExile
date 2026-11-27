using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class GlacialShieldSwipeTests
{
    [Fact]
    public async Task Swipe_expands_from_its_release_and_hits_each_target_in_the_cone_once()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "glacial-swipe-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort skill = Assert.Single(fixture.Equip(("Glacial Shield Swipe", 1)));
        var near = Target(901, (108, 100));
        var far = Target(902, (160, 100));
        var flank = Target(903, (135, 137));
        var side = Target(904, (112, 138));
        var rear = Target(905, (83, 100));
        var beyond = Target(906, (186, 100));

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.CharacterPlayerStats(player);
            player.Pools = player.Pools with { SpellBlockChance = 100 };
            Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 1,
                block: true, spell: true, suppress: false, out var block));
            Assert.True(block.Blocked);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (110, 100), sequence: 1));

            Step(.05);
            Assert.InRange(near.Life, 1u, near.MaxLife - 1);
            uint nearLife = near.Life;
            Assert.Equal(far.MaxLife, far.Life);
            Assert.Equal(flank.MaxLife, flank.Life);
            Step(.65);
            Assert.InRange(far.Life, 1u, far.MaxLife - 1);
            Assert.InRange(flank.Life, 1u, flank.MaxLife - 1);
            Assert.Equal(nearLife, near.Life);
            Assert.Equal(side.MaxLife, side.Life);
            Assert.Equal(rear.MaxLife, rear.Life);
            Assert.Equal(beyond.MaxLife, beyond.Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        SpawnedMonster Target(uint id, (int X, int Y) at)
        {
            var monster = new SpawnedMonster("Swipe target", new(at.X, at.Y, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = monster;
            player.Area.Grid.Set(new(id), at, LivingCollision.Size(monster));
            return monster;
        }

        void Step(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }
}

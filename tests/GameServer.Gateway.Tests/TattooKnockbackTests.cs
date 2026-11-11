using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The last current tattoo row of issue #372: the Tukohama Brawler's chance to knock enemies back on hit. The consumer
// pushes the monster away from the player, interrupts its action and hands the destination to the AI tick's Teleport.
public sealed class TattooKnockbackTests
{
    [Fact]
    public void The_knockback_tattoo_maps_its_client_stat()
    {
        var stats = TattooStats.For("Tattoo of the Tukohama Brawler");
        Assert.NotNull(stats);
        Assert.Contains((Program.Stats.Key("base_global_chance_to_knockback_%")!.Value, 10), stats);
    }

    [Fact]
    public void The_knockback_consumer_pushes_the_monster_away_and_queues_the_teleport()
    {
        // Away from the player, as far as the distance allows and the cells are walkable.
        Assert.Equal((0, 7), Game.KnockbackDestination((0, 0), (0, 5), 2, (_, _) => true));
        Assert.Equal((0, 6), Game.KnockbackDestination((0, 0), (0, 5), 2, (x, y) => x == 0 && y == 6));
        Assert.Null(Game.KnockbackDestination((0, 0), (0, 5), 2, (_, _) => false));
        Assert.Null(Game.KnockbackDestination((3, 3), (3, 3), 2, (_, _) => true));

        // A player hit with the tattoo's 10% chance made certain shoves a monster two cells away from the player.
        var conn = new ClientState(new(1, "tattoo-knockback", 1000, 1000, 100, 100, null, []), 1, 1);
        conn.Monsters[1] = new SpawnedMonster("kb", conn.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100);
        var stats = new Dictionary<uint, int> { [Program.Stats.Key("base_global_chance_to_knockback_%")!.Value] = 100 };
        var target = conn.Monsters[1];
        Assert.True(Game.KnockbackOnHitLocked(conn, target, stats, 0, new Random(1)));
        Assert.Equal((107, 100), target.Walk.Position(0));
        Assert.Equal((107, 100), target.KnockbackPending);

        // The next AI tick presents it as a Teleport and clears the pending cell.
        var packets = Game.MonsterAilmentChangesLocked(conn, 0);
        Assert.Contains(packets, packet => packet.Log.Contains("knocked back", StringComparison.Ordinal));
        Assert.Null(target.KnockbackPending);
    }
}

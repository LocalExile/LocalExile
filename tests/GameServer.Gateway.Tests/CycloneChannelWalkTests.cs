namespace GameServer.Gateway.Tests;

public sealed class CycloneChannelWalkTests
{
    // C→S 0x197 is the channel's aimed cell (the cursor), not the player's: a Cyclone walks toward it at its own speed,
    // and a standing channel does not move. Leaving the channel gives the walk its usual speed back.
    [Fact]
    public void Channel_update_walks_cyclone_toward_the_aim_at_its_movement_speed()
    {
        var conn = new ClientState(new(1, "cyclone-walk", 400, 400, 100, 100, null, []), 1, 1);
        conn.ApplyMovementSpeedAt(0);
        double walk = conn.Walk.Speed;

        conn.ChannelSkill = 0x10c;
        conn.ChannelMovement = (0x10c, -28);
        conn.ApplyMovementSpeedAt(0);
        Assert.Equal(walk * .72, conn.Walk.Speed, 6);
        Game.ChannelAimedAt(conn, 150, 100, 0);
        Assert.Equal((150, 100), conn.Walk.Destination);
        // 90 ms later the player is a few cells along, not on the aimed cell (the old teleport put it 50 cells away).
        Assert.InRange(conn.Walk.Position(.09).X, 100, 103);
        Assert.InRange(conn.Walk.Position(1).X, 100 + (int)(walk * .72) - 2, 100 + (int)(walk * .72) + 1);

        // C→S 0x199: the player stops where it is, at its usual speed again.
        var here = conn.Walk.Position(1);
        Game.ChannelReleased(conn, 1);
        Assert.Equal(here, conn.Walk.Destination);
        Assert.Equal((ushort)0, conn.ChannelSkill);
        Assert.Equal(walk, conn.Walk.Speed, 6);
        Assert.Equal(default, conn.ChannelMovement);
        conn.ChannelSkill = 0x10d;   // a channel that stands (Blade Flurry): its updates move nothing
        Game.ChannelAimedAt(conn, 50, 100, 2);
        Assert.Equal(here, conn.Walk.Destination);
    }
}

namespace GameServer.Gateway.Tests;

public sealed class CycloneChannelWalkTests
{
    // C→S 0x197 is the channel's aimed cell (the cursor), not the player's: a Cyclone walks toward it at its own speed,
    // and a standing channel does not move. Leaving the channel gives the walk its usual speed back.
    [Fact]
    public void Channel_update_walks_cyclone_toward_the_aim_at_its_movement_speed()
    {
        var player = new Player(new(1, "cyclone-walk", 400, 400, 100, 100, null, []), 1, 1);
        player.ApplyMovementSpeedAt(0);
        double walk = player.Walk.Speed;

        player.ChannelSkill = 0x10c;
        player.ChannelMovement = (0x10c, -28);
        player.ApplyMovementSpeedAt(0);
        Assert.Equal(walk * .72, player.Walk.Speed, 6);
        Game.ChannelAimedAt(player, 150, 100, 0);
        Assert.Equal((150, 100), player.Walk.Destination);
        // 90 ms later the player is a few cells along, not on the aimed cell (the old teleport put it 50 cells away).
        Assert.InRange(player.Walk.Position(.09).X, 100, 103);
        Assert.InRange(player.Walk.Position(1).X, 100 + (int)(walk * .72) - 2, 100 + (int)(walk * .72) + 1);

        // C→S 0x199: the player stops where it is, at its usual speed again.
        var here = player.Walk.Position(1);
        Game.ChannelReleased(player, 1);
        Assert.Equal(here, player.Walk.Destination);
        Assert.Equal((ushort)0, player.ChannelSkill);
        Assert.Equal(walk, player.Walk.Speed, 6);
        Assert.Equal(default, player.ChannelMovement);
        player.ChannelSkill = 0x10d;   // a channel that stands (Blade Flurry): its updates move nothing
        Game.ChannelAimedAt(player, 50, 100, 2);
        Assert.Equal(here, player.Walk.Destination);
    }
}

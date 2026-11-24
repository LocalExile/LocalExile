using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>The official wake zone: a monster or a corpse is in the wake zone while
/// max(|dx|, |dy|) &lt;= 172 around the player -- a square, not the old Euclid circle (171 out, 163 back).
/// The diagonal is where the square matters: (172, 172) is still awake, where the circle had already put it
/// to sleep at (121, 121). Pure geometry on Game.InWakeZone; nothing here waits on real time.</summary>
public sealed class WakeZoneTests
{
    [Fact]
    public void Square_half_side_172_holds_the_diagonal_and_stops_at_173()
    {
        Assert.True(Game.InWakeZone((172, 172), (0, 0)));
        Assert.True(Game.InWakeZone((172, 0), (0, 0)));
        Assert.True(Game.InWakeZone((0, 172), (0, 0)));
        Assert.False(Game.InWakeZone((173, 0), (0, 0)));
        Assert.False(Game.InWakeZone((173, 173), (0, 0)));
    }

    [Fact]
    public void The_zone_is_measured_from_the_player_wherever_they_stand()
    {
        Assert.True(Game.InWakeZone((200, 100), (28, 100)));
        Assert.True(Game.InWakeZone((28, 272), (28, 100)));
        Assert.False(Game.InWakeZone((201, 100), (28, 100)));
        Assert.False(Game.InWakeZone((28, 273), (28, 100)));
    }
}

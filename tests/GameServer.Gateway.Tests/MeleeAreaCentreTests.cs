namespace GameServer.Gateway.Tests;

// A melee area attack lands on its target within reach, in front of the player beyond it, and Holy Sweep's swings
// (ActionTypes Sweep, SweepAltX) around the player wherever they are aimed.
public sealed class MeleeAreaCentreTests
{
    [Fact]
    public void Sweeps_land_around_the_player_and_other_area_attacks_on_a_target_in_reach()
    {
        (int X, int Y) player = (507, 645), target = (520, 637);
        Assert.Equal(player, MeleeAreaCentre.Of(0x8f31, player, target, 51));
        Assert.Equal(player, MeleeAreaCentre.Of(0xa0b6, player, target, 51));
        Assert.Equal(target, MeleeAreaCentre.Of(0x514b, player, target, 51));
        Assert.Equal(target, MeleeAreaCentre.Of(null, player, target, 51));
        Assert.Equal(player, MeleeAreaCentre.Of(0x514b, player, target, 10));
    }
}

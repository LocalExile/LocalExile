namespace GameServer.Gateway.Tests;

// Flicker Strike goes to the clicked monster when it is in range, else to the monster in range nearest to the aimed
// point (the client's 0x141a5fa50), hostile ones only; in range is 40 cells × (1 + teleport range %) from the player, less
// the size.
public sealed class FlickerStrikeTests
{
    [Fact]
    public void Picks_the_clicked_monster_in_range_else_the_nearest_to_the_aim()
    {
        Assert.Equal(40, FlickerStrike.Range(0, false));
        Assert.Equal(50, FlickerStrike.Range(25, false));
        Assert.Equal(60, FlickerStrike.Range(0, true));
        (uint, (int, int), int, bool)[] monsters = [(1, (130, 100), 0, true), (2, (120, 100), 0, true), (3, (145, 100), 6, true),
            (4, (150, 100), 0, true), (5, (119, 100), 0, false)];
        Assert.Equal(1u, FlickerStrike.Pick(monsters, (100, 100), (121, 100), 40, clicked: 1)?.Id);
        // 4 is out of range (50 cells); 3 is 45 cells off but its size brings it to 39.
        Assert.Equal(3u, FlickerStrike.Pick(monsters, (100, 100), (150, 100), 40, clicked: 4)?.Id);
        Assert.Equal(2u, FlickerStrike.Pick(monsters, (100, 100), (118, 100), 40)?.Id);
        Assert.Null(FlickerStrike.Pick(monsters, (0, 0), (0, 0), 40));
        // A friendly monster is never a destination, clicked or nearest the aim.
        Assert.Equal(2u, FlickerStrike.Pick(monsters, (100, 100), (119, 100), 40, clicked: 5)?.Id);
        Assert.Null(FlickerStrike.Pick([monsters[4]], (100, 100), (119, 100), 40));
    }
}

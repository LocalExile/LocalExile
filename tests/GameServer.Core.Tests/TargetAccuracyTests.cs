using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class TargetAccuracyTests
{
    [Theory]
    [InlineData(-1)]
    public void Invalid_distances_are_not_close(double distance) => Assert.False(TargetAccuracy.InCloseRange(distance));

}

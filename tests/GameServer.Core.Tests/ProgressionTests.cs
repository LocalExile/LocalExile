using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class ProgressionTests
{
    // ExperienceLevels.dat's first levels.
    static readonly uint[] Starts = [0, 525, 1760, 3781, 7184];

    [Fact]
    public void Level_from_experience()
    {
        Assert.Equal(1, Progression.LevelFor(0, Starts));
        Assert.Equal(1, Progression.LevelFor(524, Starts));
        Assert.Equal(2, Progression.LevelFor(525, Starts));
        Assert.Equal(4, Progression.LevelFor(5000, Starts));
        Assert.Equal(5, Progression.LevelFor(1_000_000, Starts));
    }

}

namespace GameServer.Gateway.Tests;

public class ServerLevelsTests
{
    // A tier-16 Chateau (0x1d level 83) the client generated at seed 1174023763: tile hash 3728131682 in its
    // Client.txt and generation trace (2026-11-01). At its WorldAreas level (71) the topology pick differs
    // (chateau_1.dgr, not chateau_2.dgr) and so does the whole layout.
    [Fact]
    public void MapsAreGeneratedAtTheLevelTheyArePlayedAt()
    {
        const string chateau = "MapWorldsChateau";
        const uint seed = 1174023763;
        if (ServerLevels.For(chateau, seed) is not { } atAreaLevel) return; // needs the game files (data/game-files.zip)
        Assert.Equal(2796439874u, atAreaLevel.TileHash);

        Assert.Equal(3728131682u, ServerLevels.For(chateau, seed, 83)?.TileHash);
        Assert.Equal(seed, ServerLevels.PlayableSeed(chateau, seed, 83));
        // The lookups without a level (monster packs, heights, projectiles) get the level it is played at.
        Assert.Equal(3728131682u, ServerLevels.For(chateau, seed)?.TileHash);
    }
}

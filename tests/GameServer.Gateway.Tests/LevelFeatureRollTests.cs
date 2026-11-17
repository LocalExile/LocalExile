using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class LevelFeatureRollTests
{
    // The generated level exposes the features the generator rolled (the roll's own draws, so the same seed and stats give
    // the same list), and the stat that disables leagues (10209) leaves none of the league encounters. Needs the game files.
    [GameFilesFact]
    public void A_generated_level_exposes_its_rolled_features_and_disabled_leagues_leave_none()
    {
        var files = new TerrainCache(p => GamePack.Default.TryRead(p));
        var area = AreaInfo.Load(p => GamePack.Default.TryRead(p), "MapWorldsChateau", 83);
        var rolled = LevelGenerator.Generate(files, area, 21).Features;
        Assert.Equal(ExtraTerrainFeatures.Roll(area, new LevelRandom(21, false)), rolled);
        Assert.Equal(rolled, LevelGenerator.Generate(files, area, 21).Features);
        Assert.Contains(rolled, f => f.Name == "LegionMonolith");

        var disabled = area with { Stats = new Dictionary<int, int> { [10209] = 1 } };
        Assert.Empty(LevelGenerator.Generate(files, disabled, 21).Features);
    }
}

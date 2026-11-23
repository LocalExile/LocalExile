using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class AreaStatTerrainTests
{
    [Fact]
    public void Area_stats_steer_the_extra_terrain_features_and_none_leave_them_as_before()
    {
        // A map area with a league-chances row: a 40% Vaal side area chance (+96), a 30% labyrinth trial chance (+108).
        var area = new AreaInfo("MapTest", false, [], Hash16: 0x1234, IsMapArea: true, Level: 80,
            LeagueChance: at => at switch { 96 => 40, 108 => 30, _ => 0 });
        (List<ExtraTerrainFeatures.Feature> Features, int Draws) Roll(AreaInfo a, uint seed)
        {
            var rng = new LevelRandom(seed, false) { Log = [] };
            return (ExtraTerrainFeatures.Roll(a, rng), rng.Log!.Count);
        }

        for (uint seed = 1; seed <= 20; seed++)
        {
            var none = Roll(area, seed);
            var empty = Roll(area with { Stats = new Dictionary<int, int>() }, seed);
            Assert.Equal(none.Draws, empty.Draws);
            Assert.Equal(none.Features, empty.Features);
            Assert.Equal(2, none.Draws);   // the side area and the trial roll, nothing else at these chances

            // Stat 5945 forces the side area and 4874 the trial: no draw for either.
            var forced = Roll(area with { Stats = new Dictionary<int, int> { [5945] = 1, [4874] = 1 } }, seed);
            Assert.Equal(0, forced.Draws);
            Assert.Contains(forced.Features, f => f.Name == ExtraTerrainFeatures.VaalSideArea);
            Assert.Contains(forced.Features, f => f.Name == "LabyrinthTrial");
        }

        // is_blighted_map (10498): seven Blight encounters and nothing else; 13838 adds to the side area chance (100: no draw).
        var blighted = Roll(area with { Stats = new Dictionary<int, int> { [10498] = 1 } }, 1);
        Assert.Equal([new ExtraTerrainFeatures.Feature("BlightEncounter", 7)], blighted.Features);
        var sure = Roll(area with { Stats = new Dictionary<int, int> { [13838] = 60 } }, 1);
        Assert.Contains(sure.Features, f => f.Name == ExtraTerrainFeatures.VaalSideArea);
        Assert.Equal(1, sure.Draws);
    }
}

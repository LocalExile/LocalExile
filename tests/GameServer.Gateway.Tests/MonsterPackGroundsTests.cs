using GameServer.Core;
using GameServer.Data.Terrain.Generation;
using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>Pack grounds: the MonsterPacks <c>Grounds</c> column, a list of ground-type names, keeps a pack on its
/// own ground. The first test reads the client's rows, the second runs the placement on a hand-built ground layer.
/// Nothing here waits on real time, and neither test needs the extracted game files.</summary>
public sealed class MonsterPackGroundsTests
{
    [Fact]
    public void The_catalog_reads_the_Grounds_column_and_the_layer_answers_per_cell()
    {
        // MonsterPacks row 3, Act1-ZombieDrowned: the five beach/ocean ground types the Drowned of The Coast rise on.
        // The Coast's crab pack (row 2, Act1-CoastCrabs) lists none, as all but 155 of the 3 168 rows.
        var coast = new PackCatalog().For("1_1_2")!;
        var drowned = coast.Scattered.First(s => s.Pack.Id == "Act1-ZombieDrowned").Pack;
        Assert.Equal(
            ["BeachOceanShallowWet", "BeachOceanShallow", "BeachOceanShallowNonwalkable", "BeachOceanDeep", "BeachBottomWet"],
            drowned.Grounds);
        Assert.Null(coast.Scattered.First(s => s.Pack.Id == "Act1-CoastCrabs").Pack.Grounds);
        // Row 1122, pantheon-piety1: its only entry is "-", the column's placeholder for nothing, and no ground-type
        // file is named that — so the catalog keeps no grounds for it rather than a ground nothing matches.
        var piety = ClientTables.Table("MonsterPacks").First(p => p.Text("Id") == "pantheon-piety1");
        Assert.Equal(["-"], piety.Texts("Grounds"));
        Assert.Null(new PackCatalog().For("MapTier12_2Unique")?.Scattered.FirstOrDefault(s => s.Pack.Id == "pantheon-piety1").Pack?.Grounds);

        // The layer: a corner every 23 cells, each cell on its nearest one. Two tiles wide, wet on the left corner
        // column (0), sand on the others.
        var grid = new TileGrid(2, 2);
        for (int y = 0; y <= 2; y++)
            for (int x = 0; x <= 2; x++)
                grid.ForceGround(x, y, x == 0 ? "wet.gt" : "sand.gt");
        var layer = CellGrounds.Build(grid, path => path is null ? null
            : new GroundType(path, path == "wet.gt" ? "BeachOceanShallowWet" : "BeachBottomWet", 0));
        Assert.Equal(new HashSet<string> { "BeachOceanShallowWet", "BeachBottomWet" }, layer.Present.ToHashSet());
        // Cell 0 is on corner 0, cell 11 still is (nearest), cell 12 is already on corner 1.
        Assert.Equal("BeachOceanShallowWet", layer.NameAt(0, 0));
        Assert.Equal("BeachOceanShallowWet", layer.NameAt(11, 0));
        Assert.Equal("BeachBottomWet", layer.NameAt(12, 0));
        Assert.True(layer.On(0, 0, ["BeachOceanShallow", "BeachOceanShallowWet"]));
        Assert.False(layer.On(12, 0, ["BeachOceanShallowWet"]));
        // The column itself writes both "Knoll" (rows 548, 549) and "knoll" (rows 1195, 1202) for one ground type, so
        // the names are compared without case.
        Assert.True(layer.HasAny(["beachoceanshallowwet"]));
        Assert.False(layer.HasAny(["TempleWaterBottom"]));
    }

    [Fact]
    public void A_pack_with_grounds_stands_only_on_them()
    {
        // 20 × 20 tiles, every cell walkable: wet on the corners of the left half, sand on the right.
        const int tiles = 20, cells = tiles * CellGrounds.CellsPerTile;
        var grid = new TileGrid(tiles, tiles);
        for (int y = 0; y <= tiles; y++)
            for (int x = 0; x <= tiles; x++)
                grid.ForceGround(x, y, x < tiles / 2 ? "wet.gt" : "sand.gt");
        var layer = CellGrounds.Build(grid, path => path is null ? null
            : new GroundType(path, path == "wet.gt" ? "Wet" : "Sand", 0));
        var pack = new PackCatalog.Pack(0, "test-drowned", 3, 4, 0, 0, [], [(1, 100)], new Dictionary<int, int> { [0] = 100 },
            ["Wet"]);
        var area = new PackCatalog.AreaPacks("test_grounds", 1, [(pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);

        var notes = new List<string>();
        var on = AreaPopulation.Plan(area, cells, cells, (x, y) => true, (0, 0), [], [], seed: 4242, _ => true, notes, grounds: layer);
        Assert.NotEmpty(on);
        Assert.All(on, s => Assert.Equal("Wet", layer.NameAt(s.X, s.Y)));

        // A pack whose grounds are nowhere on the map leaves the draw; it is the only pack here, so the rule steps
        // aside rather than leaving the area empty, and says so.
        var absentNotes = new List<string>();
        var absent = AreaPopulation.Plan(area with { Scattered = [(pack with { Grounds = ["TempleWaterBottom"] }, 100)] },
            cells, cells, (x, y) => true, (0, 0), [], [], seed: 4242, _ => true, absentNotes, grounds: layer);
        Assert.NotEmpty(absent);
        Assert.Contains(absentNotes, n => n.Contains("want a ground this map has none of"));
    }
}

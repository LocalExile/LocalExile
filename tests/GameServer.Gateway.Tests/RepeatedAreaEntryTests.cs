using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class RepeatedAreaEntryTests
{
    [Fact]
    public void Both_ship_graveyard_cave_edges_keep_their_own_arrival_in_both_directions()
    {
        ushort outside = AreaTransitionCatalog.HashOf("1_1_9")!.Value;
        ushort cave = AreaTransitionCatalog.HashOf("1_1_9a")!.Value;
        var layout = new AreaLayout(cave, "1_1_9a", 300, 300, 100, 100, null,
            [(1, new(20, 30, outside) { Entrance = 1 }), (2, new(200, 210, outside) { Entrance = 2 })]);
        foreach (int source in new[] { 2, 3 })
        {
            int destination = AreaTransitionCatalog.PairedEntrance(outside, cave, source)!.Value;
            Assert.Equal(source - 1, destination);
            Assert.Equal(source, AreaTransitionCatalog.PairedEntrance(cave, outside, destination));
            Assert.Equal(source == 2 ? (28, 30) : (208, 210), layout.Arrival(outside, destination));
            var chooser = new ChooserList(cave, [1], ArrivalEntrance: destination);
            Assert.Equal(destination, chooser.ArrivalEntrance);
        }
        Assert.Equal((28, 30), layout.Arrival(outside));
        Assert.Null(AreaTransitionCatalog.PairedEntrance(outside, cave, 99));
        Assert.Null(AreaTransitionCatalog.PairedEntrance(outside, AreaTransitionCatalog.HashOf("1_1_11_1")!.Value, 4));
    }
}

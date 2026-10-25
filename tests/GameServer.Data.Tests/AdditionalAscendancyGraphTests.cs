using GameServer.Data;

namespace GameServer.Data.Tests;

public class AdditionalAscendancyGraphTests
{
    [Fact]
    public void Merge_preserves_all_ordinary_nodes_edges_roots_and_layout_and_is_idempotent()
    {
        var normal = new PassiveSkillGraph(0, [1, 6], [1, 20],
        [
            new(0, 1, 0, 3, 1, [new(1, 0, 0, [2, 10]), new(2, 1, 0, [20]), new(20, 1, 1, [])]),
            new(10, 11, 0, 0, 0, [new(10, 0, 0, [11]), new(11, 1, 1, [])])
        ]);
        var legacy = new PassiveSkillGraph(0, [1, 6], [1],
        [
            new(40, 41, 0, 0, 0, [new(1, 0, 0, [10]), new(2, 1, 0, [])]),
            new(90, 91, 0, 0, 0, [new(10, 0, 0, [12]), new(12, 1, 1, [])])
        ]);
        HashSet<uint> main = [10, 11, 12];
        var merged = AdditionalAscendancyGraph.Merge(normal, legacy, main);
        Assert.Equal([1u, 2u, 10u, 12u, 20u], merged.Nodes.Keys.Order());
        Assert.Equal(normal.Roots, merged.Roots);
        Assert.Equal(normal.Nodes[1].Connections, merged.Nodes[1].Connections);
        Assert.Equal(normal.Nodes[2].Connections, merged.Nodes[2].Connections);
        Assert.Equal(legacy.Nodes[10].Connections, merged.Nodes[10].Connections);
        Assert.Equal(normal.Groups[0].X, merged.Groups[0].X);
        Assert.Equal(normal.Groups[0].Y, merged.Groups[0].Y);
        Assert.Equal(normal.Groups[0].BackgroundOverride, merged.Groups[0].BackgroundOverride);
        Assert.Equal(normal.Groups[0].JewelPositionReference, merged.Groups[0].JewelPositionReference);
        Assert.Equal(90f, merged.Groups[1].X);
        Assert.Equal(merged.Write(), AdditionalAscendancyGraph.Merge(normal, merged, main).Write());
        var incompatible = new PassiveSkillGraph(0, [1, 7], [1], legacy.Groups);
        Assert.Throws<InvalidDataException>(() => AdditionalAscendancyGraph.Merge(normal, incompatible, main));
    }
}

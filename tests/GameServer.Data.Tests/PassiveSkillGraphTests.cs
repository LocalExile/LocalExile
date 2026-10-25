using GameServer.Data;

namespace GameServer.Data.Tests;

public class PassiveSkillGraphTests
{
    private static PassiveSkillGraph Fixture() => new(0, [1, 6, 16], [71],
    [
        new(-1.25f, 2.5f, 3, 4, 1,
        [new(71, 0, 0, [72, 72]), new(72, 1, 5, [])]),
        new(0, 0, 0, 0, 0, [new(73, 0, 0, [72])])
    ]);

    [Fact]
    public void Packed_groups_round_trip_and_incoming_edges_are_neighbours()
    {
        var original = Fixture();
        byte[] bytes = original.Write();
        var graph = PassiveSkillGraph.Read(bytes);
        Assert.Equal(bytes, graph.Write());
        Assert.Equal(-1.25f, graph.Groups[0].X);
        Assert.Equal((byte)1, graph.Groups[0].JewelPositionReference);
        Assert.Equal([71u], graph.Roots);
        Assert.Equal([71u, 73u], graph.Adjacency()[72]);
        Assert.Equal([72u], graph.Adjacency()[71]);
    }

    [Fact]
    public void Truncation_version_counts_trailing_bytes_and_invalid_edges_are_refused()
    {
        byte[] bytes = Fixture().Write();
        for (int i = 0; i < bytes.Length; i++)
            Assert.Throws<InvalidDataException>(() => PassiveSkillGraph.Read(bytes.AsSpan(0, i)));
        var version = bytes.ToArray(); version[0] = 4;
        Assert.Throws<InvalidDataException>(() => PassiveSkillGraph.Read(version));
        Assert.Throws<InvalidDataException>(() => PassiveSkillGraph.Read([.. bytes, 0]));
        Assert.Throws<InvalidDataException>(() => new PassiveSkillGraph(0, [], [],
            [new(0, 0, 0, 0, 0, [new(1, 0, 0, [2])])]));
        Assert.Throws<InvalidDataException>(() => new PassiveSkillGraph(0, [], [],
            [new(0, 0, 0, 0, 0, [new(1, 0, 0, []), new(1, 0, 0, [])])]));
        Assert.Throws<InvalidDataException>(() => PassiveSkillGraph.Read([3, 0, 0, 255, 255, 255, 255]));
    }
}

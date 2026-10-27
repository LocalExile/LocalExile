namespace GameServer.Gateway.Tests;

public class PathOfBuildingPassiveGraphTests
{
    [Fact]
    public void Packed_graph_keeps_incoming_edges_for_allocation()
    {
        var graph = PathOfBuildingPassiveGraph.Read(Pack((10, Array.Empty<uint>()), (11, new uint[] { 10 })));
        Assert.Contains(11u, graph[10]);
        Assert.Contains(10u, graph[11]);
        PathOfBuildingPassiveGraph.Validate(10, new ushort[] { 11 }, _ => false, graph);
    }

    [Fact]
    public void Disconnected_nodes_and_a_mastery_bridge_are_refused()
    {
        var graph = PathOfBuildingPassiveGraph.Read(Pack((10, new uint[] { 11 }),
            (11, new uint[] { 12 }), (12, new uint[] { 13 }), (13, Array.Empty<uint>()), (14, Array.Empty<uint>())));
        Assert.Throws<ArgumentException>(() => PathOfBuildingPassiveGraph.Validate(10, new ushort[] { 14 }, _ => false, graph));
        Assert.Throws<ArgumentException>(() => PathOfBuildingPassiveGraph.Validate(10, new ushort[] { 11, 12, 13 }, id => id == 12, graph));
        PathOfBuildingPassiveGraph.Validate(10, new ushort[] { 11, 12 }, id => id == 12, graph);
    }

    [Fact]
    public void Wrong_version_truncation_duplicate_missing_target_and_trailing_bytes_are_refused()
    {
        byte[] packed = Pack((10, new uint[] { 11 }), (11, Array.Empty<uint>()));
        var wrongVersion = packed.ToArray();
        wrongVersion[0] = 2;
        byte[][] invalid =
        [
            [], wrongVersion, packed[..^1], [.. packed, 0],
            Pack((10, Array.Empty<uint>()), (10, Array.Empty<uint>())),
            Pack((10, new uint[] { 999 })),
        ];
        foreach (byte[] bytes in invalid) Assert.Throws<InvalidDataException>(() => PathOfBuildingPassiveGraph.Read(bytes));
    }

    [Fact]
    public void An_impossible_count_is_refused_without_allocating_it()
    {
        byte[] packed = Pack((10, Array.Empty<uint>()));
        // First uint is the root count, after the three-byte version/kind/orbit header.
        Array.Fill(packed, (byte)255, 3, 4);
        Assert.Throws<InvalidDataException>(() => PathOfBuildingPassiveGraph.Read(packed));
    }

    static byte[] Pack(params (uint Id, uint[] Edges)[] nodes)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((byte)3); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((uint)1); writer.Write((uint)10);
        writer.Write((uint)1); // One group.
        writer.Write(0f); writer.Write(0f); writer.Write((uint)0); writer.Write((uint)0); writer.Write((byte)0);
        writer.Write((uint)nodes.Length);
        foreach (var (id, edges) in nodes)
        {
            writer.Write(id); writer.Write((uint)0); writer.Write((uint)0);
            writer.Write((uint)edges.Length);
            foreach (uint edge in edges) writer.Write(edge);
        }
        return output.ToArray();
    }
}

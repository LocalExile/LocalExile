using System.Collections.ObjectModel;

namespace GameServer.Data;

/// <summary>
/// The packed version-3 PoE1 passive graph. Connections in the file are directed drawing edges; allocation treats
/// them as undirected. Node metadata and ownership come from PassiveSkills.dat, joined by <see cref="Node.Id"/>.
/// </summary>
public sealed class PassiveSkillGraph
{
    public sealed record Node(uint Id, uint Orbit, uint OrbitIndex, IReadOnlyList<uint> Connections);
    public sealed record Group(float X, float Y, uint Association, uint BackgroundOverride, byte JewelPositionReference,
        IReadOnlyList<Node> Nodes);

    public byte Kind { get; }
    public IReadOnlyList<byte> Orbits { get; }
    public IReadOnlyList<uint> Roots { get; }
    public IReadOnlyList<Group> Groups { get; }
    public IReadOnlyDictionary<uint, Node> Nodes { get; }

    public PassiveSkillGraph(byte kind, IReadOnlyList<byte> orbits, IReadOnlyList<uint> roots, IReadOnlyList<Group> groups)
    {
        Kind = kind;
        Orbits = Array.AsReadOnly(orbits.ToArray());
        Roots = Array.AsReadOnly(roots.ToArray());
        Groups = Array.AsReadOnly(groups.Select(g => g with
        {
            Nodes = Array.AsReadOnly(g.Nodes.Select(n => n with
            {
                Connections = Array.AsReadOnly(n.Connections.ToArray())
            }).ToArray())
        }).ToArray());
        var nodes = new Dictionary<uint, Node>();
        foreach (var node in Groups.SelectMany(g => g.Nodes))
            if (!nodes.TryAdd(node.Id, node)) throw new InvalidDataException($"Duplicate passive graph node {node.Id}.");
        foreach (var root in Roots)
            if (!nodes.ContainsKey(root)) throw new InvalidDataException($"Passive graph root {root} is missing.");
        foreach (var node in nodes.Values)
            foreach (var target in node.Connections)
                if (!nodes.ContainsKey(target)) throw new InvalidDataException($"Passive graph edge {node.Id} -> {target} has no target.");
        Nodes = new ReadOnlyDictionary<uint, Node>(nodes);
    }

    /// <summary>Reads the exact PoE1 layout, including its one-byte group flag and four-byte connection IDs.</summary>
    public static PassiveSkillGraph Read(ReadOnlySpan<byte> bytes)
    {
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(input);
        try
        {
            byte version = reader.ReadByte();
            if (version != 3) throw new InvalidDataException($"Unsupported passive graph version {version}; expected 3.");
            byte kind = reader.ReadByte();
            var orbits = reader.ReadBytes(reader.ReadByte());
            int rootCount = Count(reader, 4);
            var roots = new uint[rootCount];
            for (int i = 0; i < roots.Length; i++) roots[i] = reader.ReadUInt32();
            int groupCount = Count(reader, 21);
            var groups = new Group[groupCount];
            for (int i = 0; i < groups.Length; i++)
            {
                float x = reader.ReadSingle(), y = reader.ReadSingle();
                uint association = reader.ReadUInt32(), background = reader.ReadUInt32();
                byte jewelReference = reader.ReadByte();
                int nodeCount = Count(reader, 16);
                var nodes = new Node[nodeCount];
                for (int j = 0; j < nodes.Length; j++)
                {
                    uint id = reader.ReadUInt32(), orbit = reader.ReadUInt32(), position = reader.ReadUInt32();
                    int connectionCount = Count(reader, 4);
                    var connections = new uint[connectionCount];
                    for (int k = 0; k < connections.Length; k++) connections[k] = reader.ReadUInt32();
                    nodes[j] = new Node(id, orbit, position, connections);
                }
                groups[i] = new Group(x, y, association, background, jewelReference, nodes);
            }
            if (input.Position != input.Length) throw new InvalidDataException("Trailing bytes in passive graph.");
            return new PassiveSkillGraph(kind, orbits, roots, groups);
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated passive graph.", ex); }
    }

    private static int Count(BinaryReader reader, int minimumBytes)
    {
        uint count = reader.ReadUInt32();
        if (count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumBytes)
            throw new InvalidDataException("Passive graph count exceeds the remaining file.");
        return (int)count;
    }

    /// <summary>Each node's sorted unique neighbours, including incoming drawing edges.</summary>
    public IReadOnlyDictionary<uint, uint[]> Adjacency()
    {
        var adjacent = Nodes.Keys.ToDictionary(id => id, _ => new SortedSet<uint>());
        foreach (var node in Nodes.Values)
            foreach (var target in node.Connections)
            {
                adjacent[node.Id].Add(target);
                adjacent[target].Add(node.Id);
            }
        return new ReadOnlyDictionary<uint, uint[]>(adjacent.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    }

    /// <summary>Writes the same packed format. Kept lossless so a decode/encode check can validate the parser.</summary>
    public byte[] Write()
    {
        if (Orbits.Count > byte.MaxValue) throw new InvalidDataException("Too many passive graph orbits.");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((byte)3);
        writer.Write(Kind);
        writer.Write((byte)Orbits.Count);
        foreach (byte orbit in Orbits) writer.Write(orbit);
        writer.Write((uint)Roots.Count);
        foreach (uint root in Roots) writer.Write(root);
        writer.Write((uint)Groups.Count);
        foreach (var group in Groups)
        {
            writer.Write(group.X);
            writer.Write(group.Y);
            writer.Write(group.Association);
            writer.Write(group.BackgroundOverride);
            writer.Write(group.JewelPositionReference);
            writer.Write((uint)group.Nodes.Count);
            foreach (var node in group.Nodes)
            {
                writer.Write(node.Id);
                writer.Write(node.Orbit);
                writer.Write(node.OrbitIndex);
                writer.Write((uint)node.Connections.Count);
                foreach (uint target in node.Connections) writer.Write(target);
            }
        }
        return output.ToArray();
    }
}

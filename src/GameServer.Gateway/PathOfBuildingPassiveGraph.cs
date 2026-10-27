using GameServer.Data.Ggpk;
using GgDump;

/// <summary>Checks imported allocations against the copied client's actual packed version-3 passive graph.</summary>
static class PathOfBuildingPassiveGraph
{
    public static IReadOnlyDictionary<uint, uint[]> Load()
        => Load("Client/Content.ggpk", "Tools/ooz/ooz_worker.mjs");

    public static IReadOnlyDictionary<uint, uint[]> Load(string ggpk, string worker, string nodeExecutable = "node")
    {
        if (!File.Exists(ggpk) || !File.Exists(worker))
            throw new ArgumentException("A nonempty passive tree requires Client/Content.ggpk and Tools/ooz/ooz_worker.mjs. " +
                "Complete Agents/GettingStarted.md and run the importer from the repository root.");
        try
        {
            using var oodle = new NodeOodleDecoder(nodeExecutable, worker);
            using var files = GameFileSystem.Open(ggpk, oodle);
            return Read(files.ReadFile("metadata/passiveskillgraph.psg"));
        }
        catch (Exception e) when (e is GgpkFormatException or System.ComponentModel.Win32Exception)
        {
            throw new ArgumentException("The copied client's passive graph could not be read. Check the 3.29.3b client, " +
                "Node.js and Tools/ooz dependencies from Agents/GettingStarted.md. " + e.Message, e);
        }
    }

    public static IReadOnlyDictionary<uint, uint[]> Read(ReadOnlySpan<byte> bytes)
    {
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(input);
        try
        {
            if (reader.ReadByte() != 3) throw new InvalidDataException("The client passive graph must use packed version 3.");
            reader.ReadByte(); // Graph kind.
            int orbitCount = reader.ReadByte();
            if (reader.ReadBytes(orbitCount).Length != orbitCount) throw new EndOfStreamException();
            int rootsCount = Count(reader, 4);
            var roots = new uint[rootsCount];
            for (int i = 0; i < rootsCount; i++) roots[i] = reader.ReadUInt32();
            var adjacency = new Dictionary<uint, HashSet<uint>>();
            int groups = Count(reader, 21);
            for (int i = 0; i < groups; i++)
            {
                reader.ReadSingle(); reader.ReadSingle();
                reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadByte();
                int nodes = Count(reader, 16);
                for (int j = 0; j < nodes; j++)
                {
                    uint id = reader.ReadUInt32();
                    reader.ReadUInt32(); reader.ReadUInt32(); // Orbit and orbit index.
                    if (!adjacency.TryAdd(id, [])) throw new InvalidDataException($"Duplicate passive graph node {id}.");
                    int connections = Count(reader, 4);
                    for (int k = 0; k < connections; k++) adjacency[id].Add(reader.ReadUInt32());
                }
            }
            if (input.Position != input.Length) throw new InvalidDataException("Trailing bytes in the passive graph.");
            foreach (uint root in roots)
                if (!adjacency.ContainsKey(root)) throw new InvalidDataException($"Missing passive graph root {root}.");
            // PSG drawing edges may be stored in either direction; allocation connectivity is undirected.
            foreach (var (id, neighbours) in adjacency.ToArray())
                foreach (uint other in neighbours.ToArray())
                {
                    if (!adjacency.TryGetValue(other, out var reverse))
                        throw new InvalidDataException($"Passive graph node {id} links to missing node {other}.");
                    reverse.Add(id);
                }
            return adjacency.ToDictionary(p => p.Key, p => p.Value.ToArray());
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated passive graph.", e); }
    }

    public static void Validate(uint start, IReadOnlyCollection<ushort> allocated,
        Func<ushort, bool> isMastery, IReadOnlyDictionary<uint, uint[]> adjacency)
    {
        if (!adjacency.ContainsKey(start) || allocated.Any(id => !adjacency.ContainsKey(id)))
            throw new ArgumentException("The selected passive tree contains a node absent from the copied client's graph.");
        var requested = allocated.Select(id => (uint)id).ToHashSet();
        var reached = new HashSet<uint> { start };
        var queue = new Queue<uint>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out uint current))
        {
            // Masteries are leaves. Reaching one never connects otherwise disconnected ordinary branches.
            if (current <= ushort.MaxValue && isMastery((ushort)current)) continue;
            foreach (uint next in adjacency[current])
                if (requested.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        }
        if (!requested.IsSubsetOf(reached))
            throw new ArgumentException("Every selected passive must connect to the character's class start through allocated ordinary nodes.");
    }

    static int Count(BinaryReader reader, int minimumBytes)
    {
        uint count = reader.ReadUInt32();
        if (count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumBytes)
            throw new InvalidDataException("Passive graph count exceeds the remaining file.");
        return (int)count;
    }
}

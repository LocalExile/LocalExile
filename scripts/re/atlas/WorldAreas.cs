using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Atlas;

sealed record AreaRow(int Index, string Id, string Name, bool IsTown, int[] Connections, ushort Hash16, string[] Topologies);
sealed record TopologyRow(int Index, string Id, string Graph);

static class WorldAreas
{
    /// <summary>Id, Name, IsTown, Connections and HASH16 straight from Data/WorldAreas.datc64 (the layout
    /// scripts/re/worldareas.py uses); TopologiesKeys from the WorldAreas client table (checked row by row by Id).</summary>
    public static (List<AreaRow> Areas, List<TopologyRow> Topologies, List<string> Problems) Load(GameFiles files, string root)
    {
        var problems = new List<string>();
        byte[] d = files.Bytes("Data/WorldAreas.datc64") ?? throw new InvalidOperationException("Data/WorldAreas.datc64 not found");
        int n = BinaryPrimitives.ReadInt32LittleEndian(d);
        int mark = d.AsSpan().IndexOf(new byte[] { 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb });
        int rowSize = (mark - 4) / n;
        byte[] data = d[mark..];
        string Str(ulong off)
        {
            var sb = new StringBuilder();
            for (int o = (int)off; o + 1 < data.Length; o += 2)
            {
                char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o));
                if (c == 0) break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        string clientDb = Path.Combine(root, "data", "client.db");
        var topologies = GameServer.Core.ClientTable.Load(clientDb, "Topologies")
            .Select(t => new TopologyRow(t.Index, t.Text("Id") ?? "", t.Text("DGRFile") ?? "")).ToList();
        var json = GameServer.Core.ClientTable.Load(clientDb, "WorldAreas");
        if (json.Count != n) problems.Add($"the WorldAreas table has {json.Count} rows, the .datc64 {n}");

        var areas = new List<AreaRow>();
        for (int i = 0; i < n; i++)
        {
            var r = d.AsSpan(4 + i * rowSize, rowSize);
            string id = Str(BinaryPrimitives.ReadUInt64LittleEndian(r));
            string name = Str(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]));
            bool town = r[20] != 0;
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(r[22..]), off = BinaryPrimitives.ReadUInt64LittleEndian(r[30..]);
            var conns = new int[count];
            for (int k = 0; k < (int)count; k++) conns[k] = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)off + 8 * k));
            ushort h16 = BinaryPrimitives.ReadUInt16LittleEndian(r[42..]);
            string[] topo = [];
            if (i < json.Count)
            {
                var j = json[i];
                if (j.Text("Id") != id) problems.Add($"row {i}: the WorldAreas table's Id {j.Text("Id")} != {id}");
                else
                    topo = j.Ints("TopologiesKeys").Where(x => x >= 0 && x < topologies.Count)
                        .Select(x => topologies[x].Id).ToArray();
            }
            areas.Add(new AreaRow(i, id, name, town, conns, h16, topo));
        }
        return (areas, topologies, problems);
    }
}

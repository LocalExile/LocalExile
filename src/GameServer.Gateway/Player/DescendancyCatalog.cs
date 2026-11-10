using System.Text.Json;

/// <summary>
/// The Descendancy.dat rows: a second ascendancy layer the client draws beside the main one (0x33's spare byte selects
/// the row). Affliction's Wildwood specialisations (Warden of the Maji, Warlock of the Mists, Wildwood Primalist) and the
/// 3.29 Bloodline classes are its rows; each node's <c>DescendancyKey</c> in PassiveSkills.dat names its row.
/// </summary>
sealed class DescendancyCatalog(string folder = "data")
{
    /// <summary>One row. <paramref name="Index"/> is the table row, the byte 0x33 carries; 0 is none.</summary>
    public sealed record Row(byte Index, string Id, string Name);

    readonly Lazy<IReadOnlyList<Row>> _rows = new(() => Load(folder));

    public IReadOnlyList<Row> Rows => _rows.Value;

    public Row? Find(string name) => Rows.FirstOrDefault(r => r.Index != 0 &&
        (r.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    readonly Lazy<IReadOnlyDictionary<byte, ushort>> _startNodes = new(() => LoadStartNodes(folder));

    /// <summary>The row's implicit start node (PassiveSkills <c>IsAscendancyStartingNode</c>), if the data has one.</summary>
    public ushort? StartNode(byte row) => _startNodes.Value.TryGetValue(row, out ushort node) ? node : null;

    static IReadOnlyDictionary<byte, ushort> LoadStartNodes(string folder)
    {
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        using var json = GameTables.Open(path);
        var starts = new Dictionary<byte, ushort>();
        foreach (var r in json.RootElement.EnumerateArray())
        {
            if (!r.TryGetProperty("DescendancyKey", out var key) || key.ValueKind != JsonValueKind.Number) continue;
            if (!r.GetProperty("IsAscendancyStartingNode").GetBoolean()) continue;
            int id = r.GetProperty("PassiveSkillGraphId").GetInt32();
            if (id is <= 0 or > ushort.MaxValue) continue;
            starts[checked((byte)key.GetInt32())] = checked((ushort)id);
        }
        return starts;
    }

    static IReadOnlyList<Row> Load(string folder)
    {
        string path = Path.Combine(folder, "tables", "Descendancy.json");
        using var json = GameTables.Open(path);
        var rows = new List<Row>();
        int index = 0;
        foreach (var r in json.RootElement.EnumerateArray())
        {
            if (index > byte.MaxValue) throw new InvalidDataException($"{path}: more than 255 rows");
            rows.Add(new(checked((byte)index++), r.GetProperty("Id").GetString() ?? "",
                r.GetProperty("Name").GetString() ?? ""));
        }
        return rows;
    }
}

using System.Text.Json;
using GameServer.Modding;

namespace GameServer.Modules.Atlas;

/// <summary>
/// The Atlas's completed maps (21-hideouts.md §4). The client keeps them in two AtlasNode sets of its instance, rebuilt
/// with every area: S→C 0x1a3 replaces them from two row masks, S→C 0x1a4 adds one node. A node in the second set is drawn
/// completed and opens its Connections. A map is completed when its WorldAreas boss, or any unique monster, dies in it;
/// the module saves the AtlasNode Ids per character.
/// </summary>
public static class AtlasProgress
{
    public sealed record Node(int Row, string Id, ushort Hash16, string Name, IReadOnlySet<string> Areas, IReadOnlySet<string> Bosses);

    const string CompletedKey = "completed";

    /// <summary>The masks' rows: the client reads 22 bytes and keeps rows below 170 (the Atlas's maps; later rows are
    /// RandomContent nodes).</summary>
    const int MaskRows = 170, MaskBytes = 22;

    static readonly Lazy<IReadOnlyList<Node>> Nodes = new(() => Load("data"));

    public static IReadOnlyList<Node> Maps => Nodes.Value;

    public static Node? Find(string idOrName) => Maps.FirstOrDefault(n =>
        n.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || n.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static Node? OfArea(string areaId) => Maps.FirstOrDefault(n => n.Areas.Contains(areaId));

    public static bool CompletesMap(Node node, MonsterInfo monster) =>
        monster.Rarity == MonsterRarity.Unique || node.Bosses.Contains(monster.VarietyId);

    public static IReadOnlySet<string> Completed(ISavedData data) =>
        (data.Get(CompletedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Saves the nodes as completed; false when they all were.</summary>
    public static bool Complete(ISavedData data, IEnumerable<Node> nodes) => data.Change(CompletedKey, old =>
    {
        var ids = (old ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        int before = ids.Count;
        ids.UnionWith(nodes.Select(n => n.Id));
        return ids.Count == before ? null : string.Join(',', ids.Order(StringComparer.Ordinal));
    });

    public static bool Reset(ISavedData data) => data.Change(CompletedKey, old => string.IsNullOrEmpty(old) ? null : "");

    /// <summary>S→C 0x1a3 with empty masks. The client rebuilds its instance per area and treats a filled 0x1a3 as a
    /// burst of completions (chat / notifications); this only clears the two AtlasNode sets. Completed maps then go
    /// out as 0x1a4.</summary>
    public static byte[] EmptyMessage() => [0x01, 0xa3, 0, 0, 0, 0x03, 0, .. new byte[MaskBytes], .. new byte[MaskBytes]];

    /// <summary>S→C 0x1a3: u8 → +0x71, u8 → +0x72, u8 → +0x70, u8 sections (bit 0: first mask, bit 1: second mask),
    /// then always 0x141fa4050 (u8 count of u16 pairs, 0 here) and the selected 22-byte masks (bit r of byte r / 8 =
    /// AtlasNode row r). Both masks hold the completed maps. Used by tests; live enter uses <see cref="EmptyMessage"/>
    /// plus <see cref="NodeMessage"/> so the client does not announce every saved map.</summary>
    public static byte[] FullMessage(IReadOnlySet<string> completed)
    {
        var mask = new byte[MaskBytes];
        foreach (var node in Maps)
            if (node.Row < MaskRows && completed.Contains(node.Id)) mask[node.Row >> 3] |= (byte)(1 << (node.Row & 7));
        return [0x01, 0xa3, 0, 0, 0, 0x03, 0, .. mask, .. mask];
    }

    /// <summary>S→C 0x1a4: u16 BE AtlasNode HASH16, u8 ×2 (unknown, 0), u8 flags (bit 1: also the second set).</summary>
    public static byte[] NodeMessage(Node node) => [0x01, 0xa4, (byte)(node.Hash16 >> 8), (byte)node.Hash16, 0, 0, 0x02];

    static IReadOnlyList<Node> Load(string folder)
    {
        string nodes = Path.Combine(folder, "tables", "AtlasNode.json"), areas = Path.Combine(folder, "tables", "WorldAreas.json"),
            varieties = Path.Combine(folder, "tables", "MonsterVarieties.json");
        if (!File.Exists(nodes) || !File.Exists(areas)) return [];
        var areaRows = new List<(string Id, string Name, int[] Bosses)>();
        using (var a = GameTables.Open(areas))
            foreach (var row in a.RootElement.EnumerateArray())
                areaRows.Add((row.GetProperty("Id").GetString() ?? "", row.GetProperty("Name").GetString() ?? "",
                    [.. row.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray().Select(b => b.GetInt32())]));
        var bossIds = new Dictionary<int, string>();
        var result = new List<(int Row, string Id, ushort Hash16, int[] Areas)>();
        using (var n = GameTables.Open(nodes))
        {
            int index = 0;
            foreach (var row in n.RootElement.EnumerateArray())
            {
                int r = index++;
                if (r >= MaskRows || row.GetProperty("NotOnAtlas").GetBoolean()
                    || !(row.GetProperty("IsNormalMap").GetBoolean() || row.GetProperty("IsUniqueMap").GetBoolean())) continue;
                int[] rowAreas = [.. new[] { "Area1", "Area2" }
                    .Select(c => row.GetProperty(c) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : -1)
                    .Where(i => i >= 0 && i < areaRows.Count).Distinct()];
                if (rowAreas.Length == 0) continue;
                result.Add((r, row.GetProperty("Id").GetString() ?? "", (ushort)row.GetProperty("HASH16").GetInt32(), rowAreas));
                foreach (int boss in rowAreas.SelectMany(i => areaRows[i].Bosses)) bossIds[boss] = "";
            }
        }
        if (bossIds.Count > 0 && File.Exists(varieties))
            using (var v = GameTables.Open(varieties))
            {
                int count = v.RootElement.GetArrayLength();
                foreach (int row in bossIds.Keys.ToList())
                    bossIds[row] = row >= 0 && row < count ? v.RootElement[row].GetProperty("Id").GetString() ?? "" : "";
            }
        return [.. result.Select(n => new Node(n.Row, n.Id, n.Hash16, areaRows[n.Areas[0]].Name,
            n.Areas.Select(i => areaRows[i].Id).ToHashSet(StringComparer.Ordinal),
            n.Areas.SelectMany(i => areaRows[i].Bosses).Select(b => bossIds.GetValueOrDefault(b, "")).Where(b => b.Length > 0)
                .ToHashSet(StringComparer.Ordinal)))];
    }
}

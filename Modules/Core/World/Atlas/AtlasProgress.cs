using System.Text.Json;
using GameServer.Modding;

namespace GameServer.Modules.Atlas;

/// <summary>
/// The Atlas's completed maps (21-hideouts.md §4). The client keeps them in two AtlasNode sets of its instance, rebuilt
/// with every area: S→C 0x1a3 replaces them from two row masks, S→C 0x1a4 adds one node. A node in the second set is drawn
/// completed and opens its Connections. A map is completed when that area's last WorldAreas boss dies in it
/// (Atziri, the Maven's Nucleus, Sirus); MapDeviceRecipes nodes (Shaper, Elder, Maven …) are included. The module
/// saves the AtlasNode Ids per character.
/// </summary>
public static class AtlasProgress
{
    public sealed record Node(int Row, string Id, ushort Hash16, string Name, IReadOnlySet<string> Areas, IReadOnlySet<string> Bosses,
        IReadOnlySet<string> FinalBosses);

    const string CompletedKey = "completed";

    /// <summary>The masks' rows: the client reads 22 bytes and keeps rows below 170 (the Atlas's maps; later rows are
    /// RandomContent nodes).</summary>
    const int MaskRows = 170, MaskBytes = 22;

    static readonly Lazy<(IReadOnlyList<Node> Maps, IReadOnlySet<int> OnAtlas)> Loaded = new(() => Load("data"));

    public static IReadOnlyList<Node> Maps => Loaded.Value.Maps;

    /// <summary>Every AtlasNode row the client draws (NotOnAtlas false, row &lt; 170), including voidstone
    /// sockets and pinnacle bosses. The first 0x1a3 mask uses this so the bottom citadels stay on screen.</summary>
    public static IReadOnlySet<int> OnAtlasRows => Loaded.Value.OnAtlas;

    public static Node? Find(string idOrName) => Maps.FirstOrDefault(n =>
        n.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || n.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static Node? OfArea(string areaId) => Maps.FirstOrDefault(n => n.Areas.Contains(areaId));

    /// <summary>The last WorldAreas boss of each of the node's areas (Atziri after her vessels, Sirus after the throne).
    /// A map with one boss uses that one.</summary>
    public static bool CompletesMap(Node node, MonsterInfo monster) =>
        node.FinalBosses.Contains(monster.VarietyId);

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

    /// <summary>S→C 0x1a3 +0x70 bit for a Voidstone slot: the client tests AtlasNode.Region, not AtlasVoidstoneSlots
    /// order. Maven (slot 0) sits in region 2, Elder (1) in 1, Tangled (2) in 3, Cleansing Fire (3) in 0. Sending
    /// slot-as-bit only raised the Elder / top-right quadrant.</summary>
    public static int RegionBit(int slot) => slot switch { 0 => 2, 1 => 1, 2 => 3, 3 => 0, _ => slot };

    /// <summary>S→C 0x1a3 +0x70 (instance +0xd0f8). Bits 0–3 are the four Atlas regions: a stone in that region's
    /// socket raises those maps' minimum tier to 16. Bit 4 is the same for every regular map (VoidstoneSlot none →
    /// drawer index 4). Only occupied sockets are set, so an empty socket still accepts C→S 0xf6.</summary>
    public static byte VoidstoneMask(IReadOnlySet<int> slots)
    {
        byte mask = 0;
        foreach (int slot in slots)
        {
            int bit = RegionBit(slot);
            if ((uint)bit < 4) mask |= (byte)(1 << bit);
        }
        if (slots.Count >= 4) mask |= 0x10;
        return mask;
    }

    /// <summary>S→C 0x1a3 with empty masks. The client rebuilds its instance per area and treats a filled 0x1a3 as a
    /// burst of completions (chat / notifications); this only clears the two AtlasNode sets. Completed maps then go
    /// out as 0x1a4. <paramref name="voidstones"/> is the +0x70 T16 mask and is kept across a progress reset.
    /// <paramref name="awakening"/> is +0x71 (socketed count, 0–4).</summary>
    public static byte[] EmptyMessage(byte voidstones = 0, byte awakening = 0) =>
        [0x01, 0xa3, awakening, 0, voidstones, 0x03, 0, .. new byte[MaskBytes], .. new byte[MaskBytes]];

    static void SetBit(byte[] mask, int row)
    {
        if ((uint)row < MaskRows) mask[row >> 3] |= (byte)(1 << (row & 7));
    }

    /// <summary>S→C 0x1a3: u8 → +0x71 (socketed count), u8 → +0x72, u8 → +0x70 (Voidstone T16 bits), u8 sections
    /// (bit 0: first mask, bit 1: second mask), then always 0x141fa4050 (u8 count of u16 pairs, 0 here) and the
    /// selected 22-byte masks (bit r of byte r / 8 = AtlasNode row r). The first mask is every on-Atlas row
    /// (voidstone sockets, Ziggurat, Abomination, Eater / Exarch) so the client keeps the bottom of the panel;
    /// the second is completed maps plus those same rows so their Connections open.</summary>
    public static byte[] FullMessage(IReadOnlySet<string> completed, byte voidstones = 0, byte awakening = 0)
    {
        var revealed = new byte[MaskBytes];
        var done = new byte[MaskBytes];
        foreach (int row in OnAtlasRows) SetBit(revealed, row);
        foreach (var node in Maps)
            if (completed.Contains(node.Id)) SetBit(done, node.Row);
        foreach (int row in OnAtlasRows) SetBit(done, row);
        return [0x01, 0xa3, awakening, 0, voidstones, 0x03, 0, .. revealed, .. done];
    }

    /// <summary>S→C 0x1a4: u16 BE AtlasNode HASH16, u8 ×2 (unknown, 0), u8 flags (bit 1: also the second set).</summary>
    public static byte[] NodeMessage(Node node) => [0x01, 0xa4, (byte)(node.Hash16 >> 8), (byte)node.Hash16, 0, 0, 0x02];

    static (IReadOnlyList<Node> Maps, IReadOnlySet<int> OnAtlas) Load(string folder)
    {
        string nodes = Path.Combine(folder, "tables", "AtlasNode.json"), areas = Path.Combine(folder, "tables", "WorldAreas.json"),
            varieties = Path.Combine(folder, "tables", "MonsterVarieties.json"),
            recipes = Path.Combine(folder, "tables", "MapDeviceRecipes.json");
        if (!File.Exists(nodes) || !File.Exists(areas)) return ([], new HashSet<int>());
        var areaRows = new List<(string Id, string Name, int[] Bosses)>();
        using (var a = GameTables.Open(areas))
            foreach (var row in a.RootElement.EnumerateArray())
                areaRows.Add((row.GetProperty("Id").GetString() ?? "", row.GetProperty("Name").GetString() ?? "",
                    [.. row.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray().Select(b => b.GetInt32())]));
        var byRow = new Dictionary<int, (string Id, ushort Hash16, string Name, HashSet<int> Areas)>();
        void Add(int r, string id, ushort hash, string name, IEnumerable<int> areaIndexes)
        {
            if (r < 0 || r >= MaskRows) return;
            if (!byRow.TryGetValue(r, out var cur))
                byRow[r] = cur = (id, hash, name, []);
            foreach (int i in areaIndexes)
                if (i >= 0 && i < areaRows.Count) cur.Areas.Add(i);
        }
        var onAtlas = new HashSet<int>();
        using (var n = GameTables.Open(nodes))
        {
            int index = 0;
            var atlas = n.RootElement.EnumerateArray().ToList();
            foreach (var row in atlas)
            {
                int r = index++;
                if (r >= MaskRows) continue;
                if (!row.GetProperty("NotOnAtlas").GetBoolean()) onAtlas.Add(r);
                if (row.GetProperty("NotOnAtlas").GetBoolean()) continue;
                bool map = row.GetProperty("IsNormalMap").GetBoolean() || row.GetProperty("IsUniqueMap").GetBoolean();
                int[] rowAreas = [.. new[] { "Area1", "Area2" }
                    .Select(c => row.GetProperty(c) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : -1)
                    .Where(i => i >= 0 && i < areaRows.Count).Distinct()];
                if (map && rowAreas.Length > 0)
                    Add(r, row.GetProperty("Id").GetString() ?? "", (ushort)row.GetProperty("HASH16").GetInt32(),
                        areaRows[rowAreas[0]].Name, rowAreas);
            }
            if (File.Exists(recipes))
            {
                using var rec = GameTables.Open(recipes);
                foreach (var row in rec.RootElement.EnumerateArray())
                {
                    if (row.GetProperty("AtlasNode") is not { ValueKind: JsonValueKind.Number } node
                        || row.GetProperty("WorldArea") is not { ValueKind: JsonValueKind.Number } area) continue;
                    int r = node.GetInt32(), a = area.GetInt32();
                    if (r < 0 || r >= atlas.Count || r >= MaskRows || a <= 0 || a >= areaRows.Count) continue;
                    var atlasRow = atlas[r];
                    if (atlasRow.GetProperty("NotOnAtlas").GetBoolean()) continue;
                    Add(r, atlasRow.GetProperty("Id").GetString() ?? "", (ushort)atlasRow.GetProperty("HASH16").GetInt32(),
                        areaRows[a].Name is { Length: > 0 } named ? named : atlasRow.GetProperty("Id").GetString() ?? "",
                        [a]);
                }
            }
        }
        var bossIds = new Dictionary<int, string>();
        foreach (int boss in byRow.Values.SelectMany(n => n.Areas.SelectMany(i => areaRows[i].Bosses)))
            bossIds[boss] = "";
        if (bossIds.Count > 0 && File.Exists(varieties))
            using (var v = GameTables.Open(varieties))
            {
                int count = v.RootElement.GetArrayLength();
                foreach (int row in bossIds.Keys.ToList())
                    bossIds[row] = row >= 0 && row < count ? v.RootElement[row].GetProperty("Id").GetString() ?? "" : "";
            }
        string Variety(int row) => bossIds.GetValueOrDefault(row, "");
        return ([.. byRow.OrderBy(kv => kv.Key).Select(kv =>
        {
            var n = kv.Value;
            var bosses = n.Areas.SelectMany(i => areaRows[i].Bosses).Select(Variety).Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var finals = n.Areas.Select(i => areaRows[i].Bosses).Where(list => list.Length > 0)
                .Select(list => Variety(list[^1])).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
            return new Node(kv.Key, n.Id, n.Hash16, n.Name,
                n.Areas.Select(i => areaRows[i].Id).ToHashSet(StringComparer.Ordinal),
                bosses, finals.Count > 0 ? finals : bosses);
        })], onAtlas);
    }
}

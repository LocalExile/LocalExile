using System.Text.Json;
using GameServer.Modding;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

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
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadAtlasProgressData()
    {
        Loaded = Load("data");
    }

    public sealed record Node(int Row, string Id, ushort Hash16, string Name, IReadOnlySet<string> Areas, IReadOnlySet<string> Bosses,
        IReadOnlySet<string> FinalBosses, IReadOnlySet<ushort> AreaHashes)
    {
        public bool IsNormalMap { get; init; }
    }

    const string CompletedKey = "completed", BonusKey = "atlas.bonusCompleted", ScryKeyPrefix = "atlas.scry.";

    /// <summary>The masks' rows: the client reads 22 bytes and keeps rows below 170 (the Atlas's maps; later rows are
    /// RandomContent nodes).</summary>
    const int MaskRows = 170, MaskBytes = 22;

    static (IReadOnlyList<Node> Maps, IReadOnlySet<int> OnAtlas) Loaded = default!;

    public static IReadOnlyList<Node> Maps => Loaded.Maps;

    /// <summary>Every AtlasNode row the client draws (NotOnAtlas false, row &lt; 170), including voidstone
    /// sockets and pinnacle bosses. The first 0x1a3 mask uses this so the bottom citadels stay on screen.</summary>
    public static IReadOnlySet<int> OnAtlasRows => Loaded.OnAtlas;

    public static Node? Find(string idOrName) => Maps.FirstOrDefault(n =>
        n.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || n.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static Node? FindByHash16(ushort hash16) => Maps.FirstOrDefault(n => n.Hash16 == hash16);

    public static Node? FindByAreaHash16(ushort areaHash16) =>
        Maps.FirstOrDefault(n => n.AreaHashes.Contains(areaHash16)) ?? FindByHash16(areaHash16);

    public static Node? OfArea(string areaId) => Maps.FirstOrDefault(n => n.Areas.Contains(areaId));

    /// <summary>The last WorldAreas boss of each of the node's areas (Atziri after her vessels, Sirus after the throne).
    /// A map with one boss uses that one.</summary>
    public static bool CompletesMap(Node node, MonsterInfo monster) =>
        node.FinalBosses.Contains(monster.VarietyId);

    static HashSet<string> ReadIds(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> Completed(ISavedData data) => ReadIds(data.Get(CompletedKey));

    // Characters from before bonus objectives already received credit for their completions.
    public static IReadOnlySet<string> BonusCompleted(ISavedData data) =>
        ReadIds(data.Get(BonusKey) ?? data.Get(CompletedKey));

    static void PreserveExistingCredit(ISavedData data)
    {
        if (data.Get(BonusKey) is not null) return;
        string completed = data.Get(CompletedKey) ?? "";
        data.Change(BonusKey, old => old is null ? completed : null);
    }

    public static bool MeetsBonusObjective(MapDevice.Run map)
    {
        var mods = map.Mods;
        if (mods.MapRarity == ItemRarity.Unique) return true;
        return map.Area.Tier switch
        {
            >= 1 and <= 5 => mods.MapRarity >= ItemRarity.Magic,
            >= 6 and <= 10 => mods.MapRarity >= ItemRarity.Rare,
            >= 11 and <= 16 => mods.MapRarity >= ItemRarity.Rare && mods.Corrupted,
            _ => false,
        };
    }

    /// <summary>Saves the nodes as completed; false when they all were.</summary>
    public static bool Complete(ISavedData data, IEnumerable<Node> nodes)
    {
        PreserveExistingCredit(data);
        return Add(data, CompletedKey, nodes);
    }

    public static bool CompleteBonus(ISavedData data, IEnumerable<Node> nodes)
    {
        PreserveExistingCredit(data);
        return Add(data, BonusKey, nodes);
    }

    static bool Add(ISavedData data, string key, IEnumerable<Node> nodes) => data.Change(key, old =>
    {
        var ids = ReadIds(old);
        int before = ids.Count;
        ids.UnionWith(nodes.Select(n => n.Id));
        return ids.Count == before ? null : string.Join(',', ids.Order(StringComparer.Ordinal));
    });

    public static bool Reset(ISavedData data)
    {
        PreserveExistingCredit(data);
        bool clearedMaps = data.Change(CompletedKey, old => string.IsNullOrEmpty(old) ? null : "");
        bool clearedBonuses = data.Change(BonusKey, old => string.IsNullOrEmpty(old) ? null : "");
        return clearedMaps || clearedBonuses;
    }

    public static bool Uncomplete(ISavedData data, Node node)
    {
        PreserveExistingCredit(data);
        bool RemoveFrom(string key) => data.Change(key, old =>
        {
            var ids = ReadIds(old);
            if (!ids.Remove(node.Id)) return null;
            return string.Join(',', ids.Order(StringComparer.Ordinal));
        });
        bool removedCompleted = RemoveFrom(CompletedKey);
        bool removedBonus = RemoveFrom(BonusKey);
        return removedCompleted || removedBonus;
    }

    // Published review builds stored each donor's destination separately. Import those links once.
    static Dictionary<string, string> ScryLinks(ISavedData data)
    {
        const string key = "atlas.scryLinks";
        string? json = data.Get(key);
        if (json is not null) return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        var links = new Dictionary<string, string>();
        foreach (var node in Maps)
            if (data.Get(ScryKeyPrefix + node.Id) is { Length: > 0 } destination)
                links[node.Id] = destination;
        data.Change(key, _ => JsonSerializer.Serialize(links));
        return links;
    }

    public static bool SetScry(ISavedData data, Node from, Node to) => Saves.Atomically(() =>
    {
        var links = ScryLinks(data);
        if (links.GetValueOrDefault(from.Id) == to.Id) return false;
        foreach (var key in links.Where(pair => pair.Key == to.Id || pair.Value == from.Id || pair.Value == to.Id)
            .Select(pair => pair.Key).ToArray())
            links.Remove(key);
        links[from.Id] = to.Id;
        data.Change("atlas.scryLinks", _ => JsonSerializer.Serialize(links));
        return true;
    });

    public static string? ScryTarget(ISavedData data, Node from) =>
        Saves.Atomically(() => ScryLinks(data).GetValueOrDefault(from.Id));

    public static void ApplyScry(MapOpening request)
    {
        if (request.Opening is not { Area.UsesAtlas: true } opening || OfArea(opening.Area.Id) is not { } target) return;
        var data = Program.WorldOf(Program.PlayerOf(request.Player)).CharacterData;
        string? donor = Saves.Atomically(() => ScryLinks(data).FirstOrDefault(p => p.Value == target.Id).Key);
        if (donor is not null && Find(donor)?.Areas.FirstOrDefault() is { } sourceArea)
            request.Opening = opening with { Area = opening.Area with { DivinationCardArea = sourceArea } };
    }

    /// <summary>S→C 0x01a5: incremental Voidstone T16 region-mask delta for one socketed/unsocketed slot (0..3).</summary>
    public static byte[] VoidstoneDeltaMessage(int slot, bool socketed) =>
        InstanceMapDevice.AtlasVoidstoneDelta((byte)RegionBit(slot), socketed);

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
    /// (bit 0: first mask, bit 1: second mask), then always 0x141faaa80 (u8 count of u16 pairs, 0 here) and the
    /// selected 22-byte masks (bit r of byte r / 8 = AtlasNode row r). The first mask carries saved completed maps
    /// (+0xd030: clears fog and opens connected maps, +0x9ca), and the second carries bonus-completed maps (+0xd070:
    /// draws the bonus completion frame +0x9cb/+0x9ce and drives the bonus counter). Uncompleted maps stay fogged
    /// and uncounted.</summary>
    public static byte[] FullMessage(IReadOnlySet<string> completed, byte voidstones = 0, byte awakening = 0) =>
        FullMessage(completed, completed, voidstones, awakening);

    public static byte[] FullMessage(IReadOnlySet<string> completed, IReadOnlySet<string> bonusCompleted,
        byte voidstones = 0, byte awakening = 0)
    {
        var revealed = new byte[MaskBytes];
        var done = new byte[MaskBytes];
        foreach (var node in Maps)
        {
            if (completed.Contains(node.Id)) SetBit(revealed, node.Row);
            if (bonusCompleted.Contains(node.Id)) SetBit(done, node.Row);
        }
        return [0x01, 0xa3, awakening, 0, voidstones, 0x03, 0, .. revealed, .. done];
    }

    /// <summary>S→C 0x1a4: u16 BE AtlasNode HASH16, u8 ×2 (unknown, 0), u8 flags (bit 1: also the second set, bonus).</summary>
    public static byte[] NodeMessage(Node node, bool bonus = true) =>
        [0x01, 0xa4, (byte)(node.Hash16 >> 8), (byte)node.Hash16, 0, 0, bonus ? (byte)0x02 : (byte)0x00];

    static (IReadOnlyList<Node> Maps, IReadOnlySet<int> OnAtlas) Load(string folder)
    {
        if (!ClientTables.Has("AtlasNode") || !ClientTables.Has("WorldAreas")) return ([], new HashSet<int>());
        var areaRows = new List<(string Id, string Name, ushort Hash16, int[] Bosses)>();
        var a = ClientTables.Table("WorldAreas");
        foreach (var row in a)
            areaRows.Add((row.Text("Id") ?? "", row.Text("Name") ?? "",
                (ushort)(row.IntOrNull("HASH16") ?? 0),
                [.. row.Ints("Bosses_MonsterVarietiesKeys")]));
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
        var nTable = ClientTables.Table("AtlasNode");
        {
            int index = 0;
            var atlas = nTable.ToList();
            foreach (var row in atlas)
            {
                int r = index++;
                if (r >= MaskRows) continue;
                if (!row.Bool("NotOnAtlas")) onAtlas.Add(r);
                if (row.Bool("NotOnAtlas")) continue;
                bool map = row.Bool("IsNormalMap") || row.Bool("IsUniqueMap");
                int[] rowAreas = [.. new[] { "Area1", "Area2" }
                    .Select(c => row.IntOrNull(c) ?? -1)
                    .Where(i => i >= 0 && i < areaRows.Count).Distinct()];
                if (map && rowAreas.Length > 0)
                    Add(r, row.Text("Id") ?? "", (ushort)row.Int("HASH16"),
                        areaRows[rowAreas[0]].Name, rowAreas);
            }
            if (ClientTables.Has("MapDeviceRecipes"))
            {
                foreach (var row in ClientTables.Table("MapDeviceRecipes"))
                {
                    if (row.IntOrNull("AtlasNode") is not int r || row.IntOrNull("WorldArea") is not int areaIndex) continue;
                    if (r < 0 || r >= atlas.Count || r >= MaskRows || areaIndex <= 0 || areaIndex >= areaRows.Count) continue;
                    var atlasRow = atlas[r];
                    if (atlasRow.Bool("NotOnAtlas")) continue;
                    Add(r, atlasRow.Text("Id") ?? "", (ushort)atlasRow.Int("HASH16"),
                        areaRows[areaIndex].Name is { Length: > 0 } named ? named : atlasRow.Text("Id") ?? "",
                        [areaIndex]);
                }
            }
        }
        var bossIds = new Dictionary<int, string>();
        foreach (int boss in byRow.Values.SelectMany(n => n.Areas.SelectMany(i => areaRows[i].Bosses)))
            bossIds[boss] = "";
        if (bossIds.Count > 0 && ClientTables.Has("MonsterVarieties"))
        {
            var v = ClientTables.Table("MonsterVarieties");
            {
                int count = v.Count;
                foreach (int row in bossIds.Keys.ToList())
                    bossIds[row] = row >= 0 && row < count ? v[row].Text("Id") ?? "" : "";
        }
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
                bosses, finals.Count > 0 ? finals : bosses,
                n.Areas.Select(i => areaRows[i].Hash16).Where(h => h != 0).ToHashSet())
                { IsNormalMap = nTable[kv.Key].Bool("IsNormalMap") };
        })], onAtlas);
    }
}

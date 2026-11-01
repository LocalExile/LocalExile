using System.Text.Json;


/// <summary>
/// Shop cosmetics without the shop: the town-portal effects the client already ships, applied as the visual of the
/// player's own portals (the map device's <c>0x008f</c> variation) instead of being bought and worn through the store.
/// <para>
/// Only the portal effects are used (<c>MicrotransactionPortalVariations</c>, named through <c>MtxTypeGameSpecific</c> →
/// <c>MtxTypes</c>, e.g. "Black Hole Portal Effect"). Armour skins, weapon skins and pets are not portal visuals: the
/// client builds those from the account's MTX slots, which no local account has.
/// </para>
/// Game data, loaded on first use; without <c>data/tables</c> the list is empty.
/// </summary>
sealed class CosmeticCatalog(string folder = "data")
{
    /// <summary>One playable cosmetic: <paramref name="Index"/> is the table's own number.</summary>
    public sealed record Cosmetic(string Name, string Id, string Ao, int Index)
    {
        /// <summary>The MtxTypeGameSpecific HASH32 (= the MtxTypes HASH32): what the Cosmetics tab's records key by,
        /// 0 when the row names none.</summary>
        public uint Hash { get; init; }

        public override string ToString() => $"{Index} {Name}";
    }

    private readonly Lazy<List<Cosmetic>> _portals = new(() => LoadPortals(folder));

    public IReadOnlyList<Cosmetic> Portals => _portals.Value;

    /// <summary>
    /// A cosmetic by its number, its exact name or Id, or a piece of its name (case-insensitive), or null. Numbers are
    /// the table's own Index, which is what <c>/cosmetic list</c> prints.
    /// </summary>
    public static Cosmetic? Find(IReadOnlyList<Cosmetic> list, string query) => Matches(list, query).FirstOrDefault();

    /// <summary>
    /// Every cosmetic a query names, so a fragment is enough: its number or its exact name or Id picks the one row,
    /// anything else every row whose name contains it (case-insensitive). An empty query matches nothing. When several
    /// match, <c>/cosmetic</c> lists them with their numbers instead of guessing which one was meant.
    /// </summary>
    public static List<Cosmetic> Matches(IReadOnlyList<Cosmetic> list, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        if (int.TryParse(query, out int index)) return [.. list.Where(c => c.Index == index)];
        var exact = list.Where(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
            || c.Id.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : [.. list.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The town-portal effects: one row per visual, its AOFile (the rig the portal plays). Portal rows carry a
    /// MtxTypeGameSpecific row index; that row's HASH32 is the MtxTypes row's HASH32, whose Name is the shop name.
    /// Rows without an AOFile are map-only or scripted variants and are skipped.
    /// </summary>
    static List<Cosmetic> LoadPortals(string folder)
    {
        string dir = Path.Combine(folder, "tables");
        if (ReadRows(Path.Combine(dir, "MicrotransactionPortalVariations.json")) is not { } rows) return [];

        var names = new Dictionary<uint, string>();
        foreach (var type in ReadRows(Path.Combine(dir, "MtxTypes.json")) ?? [])
        {
            if (!type.TryGetProperty("HASH32", out var hash) || !hash.TryGetUInt32(out uint h)) continue;
            if (!type.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            names.TryAdd(h, name.GetString() ?? "");
        }
        var gameSpecific = ReadRows(Path.Combine(dir, "MtxTypeGameSpecific.json")) ?? [];

        var list = new List<Cosmetic>();
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("AOFile", out var ao) || ao.ValueKind != JsonValueKind.String) continue;
            string path = ao.GetString() ?? "";
            if (path.Length == 0) continue;
            int index = row.TryGetProperty("Index", out var i) && i.TryGetInt32(out int n) ? n : list.Count + 1;
            string name = "";
            uint hash = 0;
            if (row.TryGetProperty("MtxTypeGameSpecific", out var gs) && gs.TryGetInt32(out int gsRow) &&
                gsRow >= 0 && gsRow < gameSpecific.Count &&
                gameSpecific[gsRow].TryGetProperty("HASH32", out var gh) && gh.TryGetUInt32(out hash))
                names.TryGetValue(hash, out name!);
            if (string.IsNullOrEmpty(name)) name = $"Portal {index}";
            list.Add(new Cosmetic(name, name, path, index) { Hash = hash });
        }
        return list;
    }

    static List<JsonElement>? ReadRows(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        // Clone: the elements outlive the document, whose buffers they would otherwise point into.
        return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())] : null;
    }
}

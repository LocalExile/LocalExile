using System.Text.Json;
using GameServer.Core;


/// <summary>
/// Shop cosmetics without the shop: the town-portal effects the client already ships, applied as the visual of the
/// player's own portals (the map device's <c>0x008f</c> variation) instead of being bought and worn through the store.
/// <para>
/// Only the portal effects are used (<c>MicrotransactionPortalVariations</c>, named through <c>MtxTypeGameSpecific</c> →
/// <c>MtxTypes</c>, e.g. "Black Hole Portal Effect"). Armour skins, weapon skins and pets are not portal visuals: the
/// client builds those from the account's MTX slots, which no local account has.
/// </para>
/// Game data, loaded on first use; without the client tables the list is empty.
/// </summary>
public sealed class CosmeticCatalog
{
    /// <summary>One playable cosmetic: <paramref name="Index"/> is the table's own number.</summary>
    public sealed record Cosmetic(string Name, string Id, string Ao, int Index)
    {
        /// <summary>The MtxTypeGameSpecific HASH32 (= the MtxTypes HASH32): what the Cosmetics tab's records key by,
        /// 0 when the row names none.</summary>
        public uint Hash { get; init; }

        public override string ToString() => $"{Index} {Name}";
    }

    private readonly Lazy<List<Cosmetic>> _portals = new(LoadPortals);

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
    static List<Cosmetic> LoadPortals()
    {
        if (ClientTables.TableOrNull("MicrotransactionPortalVariations") is not { } rows) return [];

        var names = new Dictionary<uint, string>();
        if (ClientTables.TableOrNull("MtxTypes") is { } types)
            foreach (var type in types)
                if (type.LongOrNull("HASH32") is long h && type.TextOrNull("Name") is { } name) names.TryAdd((uint)h, name);
        var gameSpecific = ClientTables.TableOrNull("MtxTypeGameSpecific");

        var list = new List<Cosmetic>();
        foreach (var row in rows)
        {
            string path = row.TextOrNull("AOFile") ?? "";
            if (path.Length == 0) continue;
            int index = row.IntOrNull("Index") ?? list.Count + 1;
            string name = "";
            uint hash = 0;
            if (row.IntOrNull("MtxTypeGameSpecific") is int gsRow && gameSpecific?.Row(gsRow) is { } gs
                && gs.LongOrNull("HASH32") is long gh)
            {
                hash = (uint)gh;
                names.TryGetValue(hash, out name!);
            }
            if (string.IsNullOrEmpty(name)) name = $"Portal {index}";
            list.Add(new Cosmetic(name, name, path, index) { Hash = hash });
        }
        return list;
    }

}

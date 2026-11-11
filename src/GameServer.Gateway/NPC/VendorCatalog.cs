using GameServer.Core;

public sealed class VendorCatalog
{
    public sealed record Shop(string Kind, int Act, int TownLevel);
    public sealed record GemRule(string Vendor, int Class, string Base, int[] Offers);
    static readonly Lazy<VendorCatalog> DefaultCatalog = new(() => new());
    public static VendorCatalog Default => DefaultCatalog.Value;
    readonly Dictionary<string, Shop> shops;
    readonly Dictionary<string, string[][][]> layouts;
    readonly ILookup<string,GemRule> byVendor;
    // client.db's vendor_* tables (data/vendor-stock.json from extract-data.ps1's vendor-stock generator).
    VendorCatalog()
    {
        shops = ClientTables.Table("vendor_shops").ToDictionary(r => r.Text("npc")!,
            r => new Shop(r.Text("Kind")!, r.Int("Act"), r.Int("TownLevel")), StringComparer.Ordinal);
        byVendor = ClientTables.Table("vendor_gems").Select(r => new GemRule(r.Text("Vendor")!, r.Int("Class"), r.Text("Base")!, r.Ints("Offers")))
            .ToLookup(g => g.Vendor,StringComparer.Ordinal);
        layouts = ClientTables.Table("vendor_gem_layouts").GroupBy(r => r.Text("vendor")!, StringComparer.Ordinal)
            .ToDictionary(v => v.Key, v => v.GroupBy(r => r.Int("tab")).OrderBy(t => t.Key)
                .Select(t => t.OrderBy(r => r.Int("row")).Select(r => r.Texts("gems")).ToArray()).ToArray(), StringComparer.Ordinal);
    }
    public Shop? Find(string npc) => shops.GetValueOrDefault(npc);
    public IEnumerable<GemRule> Gems(string vendor) => byVendor[vendor];
    public string[][][] GemLayout(string npc)
    {
        var shop = Find(npc);
        string vendor = shop?.Kind == "Gems" ? "Lilly Roth" : shop?.Act switch
        { 1 => "Nessa", 2 => "Yeena", 3 => "Clarissa", 4 => "Petarus and Vanja", _ => "Lilly Roth" };
        return layouts[vendor];
    }
}

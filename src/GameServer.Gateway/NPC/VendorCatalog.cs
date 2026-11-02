using System.Text.Json;

sealed class VendorCatalog
{
    public sealed record Shop(string Kind, int Act, int TownLevel);
    public sealed record GemRule(string Vendor, int Class, string Base, int[] Offers);
    sealed record Data(int Version, Dictionary<string, Shop> Shops, GemRule[] Gems,
        Dictionary<string, string[][][]> GemLayouts);
    static readonly Lazy<VendorCatalog> DefaultCatalog = new(() => new("data"));
    public static VendorCatalog Default => DefaultCatalog.Value;
    readonly Data data;
    public VendorCatalog(string folder) => data = JsonSerializer.Deserialize<Data>(
        File.ReadAllText(Path.Combine(folder, "vendor-stock.json")))!;
    public Shop? Find(string npc) => data.Shops.GetValueOrDefault(npc);
    public IEnumerable<GemRule> Gems(string vendor) => data.Gems.Where(g => g.Vendor == vendor);
    public string[][][] GemLayout(string npc)
    {
        var shop = Find(npc);
        string vendor = shop?.Kind == "Gems" ? "Lilly Roth" : shop?.Act switch
        { 1 => "Nessa", 2 => "Yeena", 3 => "Clarissa", 4 => "Petarus and Vanja", _ => "Lilly Roth" };
        return data.GemLayouts[vendor];
    }
}

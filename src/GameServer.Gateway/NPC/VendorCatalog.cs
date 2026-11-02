using System.Text.Json;

sealed class VendorCatalog
{
    public sealed record Shop(string Kind, int Act, int TownLevel);
    public sealed record GemRule(string Vendor, int Class, string Base, int[] Offers);
    sealed record Data(int Version, Dictionary<string, Shop> Shops, GemRule[] Gems);
    static readonly Lazy<VendorCatalog> DefaultCatalog = new(() => new("data"));
    public static VendorCatalog Default => DefaultCatalog.Value;
    readonly Data data;
    public VendorCatalog(string folder) => data = JsonSerializer.Deserialize<Data>(
        File.ReadAllText(Path.Combine(folder, "vendor-stock.json")))!;
    public Shop? Find(string npc) => data.Shops.GetValueOrDefault(npc);
    public IEnumerable<GemRule> Gems(string vendor) => data.Gems.Where(g => g.Vendor == vendor);
}

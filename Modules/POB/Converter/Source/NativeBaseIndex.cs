static class NativeBaseIndex
{
    sealed record Data(ItemCatalog.BaseType[] All, Dictionary<string, ItemCatalog.BaseType[]> Names);
    static readonly Dictionary<ItemCatalog, Data> Cache = new();
    static Data Get(ItemCatalog catalog)
    {
        if (Cache.TryGetValue(catalog, out var data)) return data;
        var all = catalog.All.Where(t => t.Supported && !t.Id.Contains("Royale", StringComparison.OrdinalIgnoreCase)).ToArray();
        return Cache[catalog] = new(all, all.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase));
    }
    public static ItemCatalog.BaseType[] All(ItemCatalog catalog) => Get(catalog).All;
    public static ItemCatalog.BaseType[] Named(ItemCatalog catalog, string name) => Get(catalog).Names.GetValueOrDefault(name) ?? [];
}

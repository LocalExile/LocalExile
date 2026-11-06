using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

static class NativeGemCache
{
    static readonly Dictionary<(ItemCatalog, string, PathOfBuildingGem), Item> Cache = new();
    public static Item Create(PathOfBuildingGem gem, ItemCatalog catalog, string folder, Func<Item> create)
    {
        var key = (catalog, folder, gem);
        if (Cache.TryGetValue(key, out var item)) return item;
        return Cache[key] = create();
    }
}

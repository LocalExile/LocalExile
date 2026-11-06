using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The map tab's pages (#477): the 191 pages the client asks for, the next page once one is full, and a page only
/// taking the maps that belong to it.</summary>
public class MapStashPageTests
{
    static AccountStash OpenMapStash(ItemCatalog catalog)
    {
        Saves.Open(Path.Combine(Path.GetTempPath(), "LocalExile-mapstash-" + Guid.NewGuid().ToString("N") + ".db"));
        return AccountStash.For("MapStash" + Guid.NewGuid().ToString("N"), catalog);
    }

    static Func<Item, PlayerInventories.Shape?> ShapeOf(ItemCatalog catalog) =>
        i => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Fact]
    public void The_map_tab_gets_191_pages_and_the_next_page_once_one_is_full()
    {
        var catalog = new ItemCatalog("data");
        var stash = OpenMapStash(catalog);
        var shapeOf = ShapeOf(catalog);
        int mapTab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Map && stash.PageKeyOf(i) == 0);
        // 20 map bases x 6 pages, 23 legendary maps x 1, 4 conquerors and 4 elder guardians x 6.
        Assert.Equal(191, stash.MapChildren(mapTab).Count);
        Assert.Equal(20, catalog.Stash.MapBases.Count);
        Assert.Equal(23, catalog.Stash.LegendaryMaps.Count);
        var tierOne = catalog.All.First(t => catalog.Stash.MapTier(t.Hash) == 1);
        var map = ItemCatalog.Create(tierOne);
        int page0 = stash.MapDestination(mapTab, map, shapeOf);
        Assert.True(stash.PageKeyOf(page0) == AccountStash.MapPageKey(0, tierOne.Hash));
        // Fill the 12 x 6 cells of the first page: the next map goes to page 1 instead of being refused.
        for (int i = 0; i < 72; i++)
            Assert.NotNull(stash.Place(page0, i % 12, i / 12,
                new InventoryItem((uint)(1000 + i), (byte)(i % 12), (byte)(i / 12), ItemCatalog.Create(tierOne)), shapeOf, out _));
        int next = stash.MapDestination(mapTab, map, shapeOf);
        Assert.True(stash.PageKeyOf(next) != stash.PageKeyOf(page0));
        Assert.True(stash.PageKeyOf(next) == AccountStash.MapPageKey(1, tierOne.Hash));
    }

    [Fact]
    public void A_page_only_takes_its_own_maps_and_a_guardian_map_finds_its_guardian_page()
    {
        var catalog = new ItemCatalog("data");
        var stash = OpenMapStash(catalog);
        var shapeOf = ShapeOf(catalog);
        int mapTab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Map && stash.PageKeyOf(i) == 0);
        var tierOne = catalog.All.First(t => catalog.Stash.MapTier(t.Hash) == 1);
        var tierFour = catalog.All.First(t => catalog.Stash.MapTier(t.Hash) == 4);
        int tierFourPage = stash.MapDestination(mapTab, ItemCatalog.Create(tierFour), shapeOf);
        Assert.Equal(0, AccountStash.MapGroupOf(stash.PageKeyOf(tierFourPage)));
        // A tier-1 map addressed straight at the tier-4 page is refused there.
        Assert.False(stash.Holds(tierFourPage, ItemCatalog.Create(tierOne)));
        // A guardian's identity comes from its map modifier: elder guardian 2 (MapElderContainsBoss2) and conqueror 1.
        ushort elderTwo = catalog.Stash.GuardianModHash(3, 2);
        ushort conquerorOne = catalog.Stash.GuardianModHash(2, 1);
        Assert.NotEqual((ushort)0, elderTwo);
        Assert.NotEqual((ushort)0, conquerorOne);
        var elderMap = ItemCatalog.Create(tierOne) with { Mods = [new ItemMod(ModSection.Explicit, elderTwo, [2])] };
        var conquerorMap = ItemCatalog.Create(tierOne) with { Mods = [new ItemMod(ModSection.Explicit, conquerorOne, [1])] };
        int elderPage = stash.MapDestination(mapTab, elderMap, shapeOf);
        int conquerorPage = stash.MapDestination(mapTab, conquerorMap, shapeOf);
        Assert.Equal(3, AccountStash.MapGroupOf(stash.PageKeyOf(elderPage)));
        Assert.Equal(2u, AccountStash.MapTypeHashOf(stash.PageKeyOf(elderPage)));
        Assert.Equal(2, AccountStash.MapGroupOf(stash.PageKeyOf(conquerorPage)));
        Assert.Equal(1u, AccountStash.MapTypeHashOf(stash.PageKeyOf(conquerorPage)));
        // Neither page takes the other guardian's map, and an ordinary page takes neither.
        Assert.False(stash.Holds(conquerorPage, elderMap));
        Assert.False(stash.Holds(elderPage, conquerorMap));
        Assert.False(stash.Holds(tierFourPage, elderMap));
        // A non-map stays on the tab.
        var notAMap = ItemCatalog.Create(catalog.All.First(t => catalog.Stash.MapTier(t.Hash) is null && t.Class == "StackableCurrency"));
        Assert.Equal(mapTab, stash.MapDestination(mapTab, notAMap, shapeOf));
    }
}

using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

/// <summary>The map tab's pages (#477): the 191 pages the client asks for, the next page once one is full, and a page only
/// taking the maps that belong to it.</summary>
public class MapStashPageTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    static AccountStash OpenMapStash(ItemCatalog catalog, string? account = null)
    {
        TestEnvironment.OpenOwnSaves("mapstash");
        return AccountStash.For(account ?? "MapStash" + Guid.NewGuid().ToString("N"), catalog);
    }

    static Func<Item, PlayerInventories.Shape?> ShapeOf(ItemCatalog catalog) =>
        i => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Fact]
    public async Task The_map_tab_gets_191_pages_and_the_next_page_once_one_is_full()
    {
        var catalog = new ItemCatalog("data");
        string account = "MapStashCursor" + Guid.NewGuid().ToString("N");
        var stash = OpenMapStash(catalog, account);
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

        var conn = new ClientState(new(ushort.MaxValue, "MapStashTest", 128, 128, 32, 32, null, []), 0, 1)
            { Account = account, Character = account, League = "Standard" };
        var items = PlayerInventories.For(account, catalog);
        conn.StashTabsSent.UnionWith([mapTab, next]);
        using var wire = new Wire();
        try
        {
            // Parent-tab drops must use a free cell and publish the child inventory, including the second drop.
            for (int i = 1; i <= 2; i++)
            {
                var held = items.Add(PlayerInventories.Cursor, 0, 0, map with { Quality = (byte)i });
                await Game.PlaceInStash("map-stash-test", wire.Stream, wire.Ciphers, conn, mapTab,
                    new InstanceItems.PlaceItem(stash.InventoryOf(mapTab), 0, 0, 0, 0, 0, 0));
                Assert.Empty(items.Items(PlayerInventories.Cursor));
                Assert.Equal(i, stash.Inventory(next).Items.Count);
                Assert.Empty(stash.Inventory(mapTab).Items);
                var placed = stash.Inventory(next).Items.Single(it => it.Item.Quality == i);
                Assert.Equal(0, placed.X);
                Assert.Equal(i - 1, placed.Y);
                var changed = InstanceItems.InventoryChange(stash.InventoryOf(next), (uint)i, [], [placed]);
                Assert.Equal(changed, wire.Read(changed.Length));
                var cleared = InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                    (uint)i, [held.Id], []);
                Assert.Equal(cleared, wire.Read(cleared.Length));
            }
        }
        finally
        {
            PlayerInventories.Forget(account);
            AccountStash.Forget(account);
        }
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
        var elderMap = ItemCatalog.Create(tierOne) with { Mods = [catalog.Mods.ByHash(elderTwo)!.Roll(ModSection.Implicit, new Random(1))] };
        var conquerorMap = ItemCatalog.Create(tierOne) with { Mods = [catalog.Mods.ByHash(conquerorOne)!.Roll(ModSection.Implicit, new Random(1))] };
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
        var equipment = ItemCatalog.Create(catalog.All.First(t => t.Supported && t.Components.Contains("Armour")));
        foreach (var influence in new[] { ItemInfluence.Crusader, ItemInfluence.Redeemer, ItemInfluence.Hunter,
            ItemInfluence.Warlord, ItemInfluence.Elder })
        {
            var influenced = equipment with { Influences = influence };
            Assert.Equal(mapTab, stash.MapDestination(mapTab, influenced, shapeOf));
            Assert.All(stash.MapChildren(mapTab), page => Assert.False(stash.Holds(page, influenced)));
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            reader.ReceiveTimeout = 5000;
        }

        public byte[] Read(int count)
        {
            var bytes = new byte[count];
            reader.GetStream().ReadExactly(bytes);
            decrypt.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}

using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

/// <summary>A stash tab that a tab switch unloaded is loaded again before a change to it goes out (#710): the panel shows a
/// tab it has shown before without asking for it, and a 0xbf for an inventory the client no longer has aborts the
/// game.</summary>
public class StashTabReloadTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public async Task A_split_in_a_tab_that_a_tab_switch_unloaded_loads_the_tab_again_first()
    {
        var catalog = new ItemCatalog("data");
        TestEnvironment.OpenOwnSaves("stashreload");
        string account = "StashReload" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        var items = PlayerInventories.For(account, catalog);
        // A stack that is not shop currency: a tab holding that is never unloaded (Game.PreloadsForPrices).
        var card = catalog.All.First(t => t.Class == "DivinationCard" && t.MaxStack >= 2);
        Assert.NotNull(stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, ItemCatalog.Create(card) with { StackSize = 2 }),
            i => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null, out _));
        uint stack = stash.Inventory(0).Items.Single().Id;
        var conn = new ClientState(new(ushort.MaxValue, "StashReloadTest", 128, 128, 32, 32, null, []), 0, 1)
            { Account = account, Character = account, League = "Standard" };
        uint inventory = stash.InventoryOf(0);
        using var wire = new Wire();
        try
        {
            // The panel shows tab 0, then tab 1, which unloads tab 0 (0xc1) and marks it not loaded in the tab list.
            await Game.SendStashTab("stash-reload-test", wire.Stream, wire.Ciphers, conn, 0, InstanceStash.Personal);
            wire.Expect(InstanceInventories.AddInventory(inventory, stash.Inventory(0).Slot, 0, stash.Inventory(0).Items));
            wire.Expect(InstanceStash.TabLoaded(0, inventory));
            await Game.SendStashTab("stash-reload-test", wire.Stream, wire.Ciphers, conn, 1, InstanceStash.Personal);
            wire.Expect(InstanceInventories.RemoveInventory(inventory));
            wire.Expect(InstanceInventories.AddInventory(stash.InventoryOf(1), stash.Inventory(1).Slot, 0, stash.Inventory(1).Items));
            wire.Expect(InstanceStash.TabList(stash.Records(null, conn.StashTabsSent)));
            wire.Expect(InstanceStash.TabLoaded(1, stash.InventoryOf(1)));
            Assert.DoesNotContain(0, conn.StashTabsSent);

            // The player goes back to tab 0, which the client shows from the page it kept, and splits the stack there.
            await Game.SplitStack("stash-reload-test", wire.Stream, wire.Ciphers, conn, new InstanceItems.SplitStack(inventory, stack, 1, 3), "");
            var left = stash.Inventory(0).Items.Single();
            var held = items.Items(PlayerInventories.Cursor).Single();
            Assert.Equal((1, 1), (left.Item.StackSize, held.Item.StackSize));
            wire.Expect(InstanceInventories.AddInventory(inventory, stash.Inventory(0).Slot, 0, [left]));
            wire.Expect(InstanceStash.TabLoaded(0, inventory));
            wire.Expect(InstanceItems.InventoryChange(inventory, 1, [stack], [left]));
            wire.Expect(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor), 1, [], [held]));
            Assert.Contains(0, conn.StashTabsSent);
        }
        finally
        {
            PlayerInventories.Forget(account);
            AccountStash.Forget(account);
        }
    }

    [Fact]
    public async Task A_repeated_unique_parent_request_keeps_its_categories_registered_until_switching_away()
    {
        var catalog = new ItemCatalog("data");
        TestEnvironment.OpenOwnSaves("uniquereopen");
        string account = "UniqueReopen" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        try
        {
            ushort parent = (ushort)Enumerable.Range(0, stash.Count)
                .Single(i => stash.TabType(i) == StashRules.Unique && stash.UniqueParent(i) == i);
            var children = stash.UniqueChildren(parent).ToArray();
            Assert.NotEmpty(children);
            var inventories = children.ToDictionary(t => t, stash.InventoryOf);
            ushort currencyTab = (ushort)Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Currency);
            int mapParent = Enumerable.Range(0, stash.Count)
                .Single(i => stash.TabType(i) == StashRules.Map && stash.UniqueParent(i) == i);
            ushort mapChild = (ushort)stash.MapChildren(mapParent).First();
            Assert.NotEqual(parent, stash.UniqueParent(mapChild));
            // A real shop currency in both supported price sources: a grid tab and the dedicated currency tab.
            var currency = catalog.All.Single(t => t.Id == "Metadata/Items/Currency/CurrencyIdentification");
            var money = ItemCatalog.Create(currency) with { StackSize = 2 };
            foreach (int tab in new[] { 0, (int)currencyTab })
                Assert.NotNull(stash.Place(tab, 0, 0, new InventoryItem((uint)(tab + 1), 0, 0, money),
                    i => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null, out _));
            var payments = new[] { 0, (int)currencyTab }.ToDictionary(t => t, t => stash.Inventory(t).Items.Single());
            var conn = new ClientState(new(ushort.MaxValue, "UniqueReopenTest", 128, 128, 32, 32, null, []), 0, 1)
            {
                Account = account, Character = account, League = "Standard",
                StashForcedTab = parent, StashForcedUntil = double.PositiveInfinity
            };
            using var wire = new Wire();

            // The initial window protects queued requests; a hidden Map page is a different family's control.
            foreach (ushort tab in new ushort[] { 0, 1, currencyTab, mapChild, parent })
            {
                await Game.SendStashTab("unique-reopen-test", wire.Stream, wire.Ciphers, conn, tab, InstanceStash.Personal);
                ExpectLoaded(wire, stash, tab, register: true);
                if (tab == parent)
                    foreach (int child in children) ExpectLoaded(wire, stash, child, register: true);
            }
            Assert.Equal(parent, stash.LastTab);
            Assert.Contains(0, conn.StashTabsSent);
            Assert.Contains(1, conn.StashTabsSent);

            // Replay the parent's request after the first-open window, without sleeping or a native panel.
            conn.StashForcedUntil = double.NegativeInfinity;
            var otherTabs = conn.StashTabsSent.Where(t => t == 1 || t == mapChild).ToArray();
            Assert.Equal(2, otherTabs.Length);
            await Game.SendStashTab("unique-reopen-test", wire.Stream, wire.Ciphers, conn, parent, InstanceStash.Personal);
            foreach (int tab in otherTabs) wire.Expect(InstanceInventories.RemoveInventory(stash.InventoryOf(tab)));
            wire.Expect(InstanceStash.TabList(stash.Records(null, conn.StashTabsSent)));
            ExpectLoaded(wire, stash, parent, register: false);
            foreach (int child in children) ExpectLoaded(wire, stash, child, register: false);
            foreach (var (tab, payment) in payments)
            {
                Assert.Contains(tab, conn.StashTabsSent);
                Assert.Equal(payment, stash.Inventory(tab).Items.Single());
                Assert.True(Game.PreloadsForPrices(stash.TabType(tab), stash.Inventory(tab).Items, catalog));
            }
            Assert.DoesNotContain(1, conn.StashTabsSent);
            Assert.DoesNotContain((int)mapChild, conn.StashTabsSent);
            Assert.Equal(0u, stash.Records(null, conn.StashTabsSent)[mapChild].Inventory);
            foreach (var row in stash.Records(null, conn.StashTabsSent).Where(r => inventories.ContainsKey(r.Index)))
            {
                Assert.True(row.Loaded);
                Assert.Equal(inventories[row.Index], row.Inventory);
            }

            // Leaving the collection still unloads the whole old family, while retaining shop currency.
            var leaving = conn.StashTabsSent.Where(t => !payments.ContainsKey(t)).ToArray();
            await Game.SendStashTab("unique-reopen-test", wire.Stream, wire.Ciphers, conn, 1, InstanceStash.Personal);
            foreach (int tab in leaving) wire.Expect(InstanceInventories.RemoveInventory(stash.InventoryOf(tab)));
            wire.Expect(InstanceInventories.AddInventory(stash.InventoryOf(1), stash.Inventory(1).Slot, 0, stash.Inventory(1).Items));
            wire.Expect(InstanceStash.TabList(stash.Records(null, conn.StashTabsSent)));
            ExpectLoaded(wire, stash, 1, register: false);
            Assert.True(conn.StashTabsSent.SetEquals([0, 1, currencyTab]));
            Assert.Equal(4, stash.CountOf(currency.Hash));

            // A payment can still change both retained inventories directly, without another C0/C8 registration.
            var paid = stash.Pay(currency.Hash, 4);
            Assert.NotNull(paid);
            Assert.Equal(payments.Keys.Order(), paid.Tabs.Keys.Order());
            foreach (var (tab, payment) in payments)
            {
                Assert.Equal(new[] { payment.Id }, paid.Tabs[tab].Removed);
                Assert.Empty(paid.Tabs[tab].Changed);
                await Game.SendTabChange("unique-reopen-test", wire.Stream, wire.Ciphers, conn, stash, tab,
                    paid.Tabs[tab].Removed, paid.Tabs[tab].Changed, null);
                wire.Expect(InstanceItems.InventoryChange(stash.InventoryOf(tab), 1, [payment.Id], []));
            }
            Assert.Equal(0, stash.CountOf(currency.Hash));
            Assert.True(conn.StashTabsSent.SetEquals([0, 1, currencyTab]));
        }
        finally
        {
            AccountStash.Forget(account);
        }
    }

    [Fact]
    public async Task Explicit_unique_refresh_announces_only_known_hidden_inventories_before_re_registering_categories()
    {
        var catalog = new ItemCatalog("data");
        TestEnvironment.OpenOwnSaves("uniquerefresh");
        string account = "UniqueRefresh" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        try
        {
            int parent = Enumerable.Range(0, stash.Count)
                .Single(i => stash.TabType(i) == StashRules.Unique && stash.UniqueParent(i) == i);
            var children = stash.UniqueChildren(parent).ToArray();
            Assert.True(children.Length > 1);
            int knownChild = children[^1];
            var conn = new ClientState(new(ushort.MaxValue, "UniqueRefreshTest", 128, 128, 32, 32, null, []), 0, 1)
                { Account = account, Character = account, League = "Standard" };
            using var wire = new Wire();
            // Establish the known inventory by the actual child request, not by seeding StashTabsSent.
            await Game.SendStashTab("unique-refresh-test", wire.Stream, wire.Ciphers, conn, (ushort)knownChild, InstanceStash.Personal);
            ExpectLoaded(wire, stash, knownChild, register: true);
            Assert.True(conn.StashTabsSent.SetEquals([knownChild]));

            // UniqueStashFillTests covers none/all loaded. Here the real request leaves a partially loaded family.
            var before = stash.Records(null, conn.StashTabsSent);
            foreach (int child in children)
            {
                Assert.NotEqual(0ul, before[child].HiddenKey);
                Assert.Equal(parent, before[child].Parent);
                Assert.Equal(child == knownChild, before[child].Loaded);
                Assert.Equal(child == knownChild ? stash.InventoryOf(child) : 0u, before[child].Inventory);
            }
            Assert.All(before.Where(r => r.HiddenKey != 0 && r.Inventory != 0), r => Assert.Equal(knownChild, r.Index));
            await Game.RefreshUniqueCollection("unique-refresh-test", wire.Stream, wire.Ciphers, conn, stash, parent);
            wire.Expect(InstanceStash.TabList(before));
            foreach (int child in children)
            {
                if (child == knownChild) wire.Expect(InstanceInventories.RemoveInventory(stash.InventoryOf(child)));
                ExpectLoaded(wire, stash, child, register: true);
            }
            Assert.True(conn.StashTabsSent.SetEquals(children));

            // An explicit refresh of an entirely known family must still remove each child before C0/C8.
            // The reopen-retention rule must not turn this deliberate refresh into a no-op.
            before = stash.Records(null, conn.StashTabsSent);
            Assert.All(children, child =>
            {
                Assert.True(before[child].Loaded);
                Assert.Equal(stash.InventoryOf(child), before[child].Inventory);
            });
            await Game.RefreshUniqueCollection("unique-refresh-test", wire.Stream, wire.Ciphers, conn, stash, parent);
            wire.Expect(InstanceStash.TabList(before));
            foreach (int child in children)
            {
                wire.Expect(InstanceInventories.RemoveInventory(stash.InventoryOf(child)));
                ExpectLoaded(wire, stash, child, register: true);
            }
            Assert.True(conn.StashTabsSent.SetEquals(children));
        }
        finally
        {
            AccountStash.Forget(account);
        }
    }

    static void ExpectLoaded(Wire wire, AccountStash stash, int tab, bool register)
    {
        if (register)
        {
            var (slot, items) = stash.Inventory(tab);
            wire.Expect(InstanceInventories.AddInventory(stash.InventoryOf(tab), slot, 0, items));
        }
        wire.Expect(InstanceStash.TabLoaded((ushort)tab, stash.InventoryOf(tab)));
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

        /// <summary>The next message on the wire is this one.</summary>
        public void Expect(byte[] message)
        {
            var bytes = new byte[message.Length];
            reader.GetStream().ReadExactly(bytes);
            decrypt.Receive.Process(bytes);
            Assert.Equal(Convert.ToHexString(message), Convert.ToHexString(bytes));
        }

        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}

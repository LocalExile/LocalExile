using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

/// <summary>Visited stash tabs stay registered in the client's inventory manager across tab switches so revisiting a tab
/// does not leave its cached UI page (+0x58) with a missing inventory (which blocks manual placement with a red overlay);
/// any change to a still-unopened tab still loads it first via SendTabChange (#605, #710).</summary>
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
        // A stack that is not shop currency (not preloaded by Game.PreloadsForPrices).
        var card = catalog.All.First(t => t.Class == "DivinationCard" && t.MaxStack >= 2);
        Assert.NotNull(stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, ItemCatalog.Create(card) with { StackSize = 2 }),
            i => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null, out _));
        uint stack = stash.Inventory(0).Items.Single().Id;
        var player = new Player(new(ushort.MaxValue, "StashReloadTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = { Account = account, Name = account, League = "Standard" } };
        uint inventory = stash.InventoryOf(0);
        using var wire = new Wire();
        player.Connection.Session = new("stash-test", wire.Stream, wire.Ciphers);
        try
        {
            // Showing tab 0 then tab 1 keeps tab 0 registered (no 0xc1 RemoveInventory and no 0x136 TabList).
            await Game.SendStashTab(player, 0, InstanceStash.Personal);
            wire.Expect(InstanceInventories.AddInventory(inventory, stash.Inventory(0).Slot, 0, stash.Inventory(0).Items));
            wire.Expect(InstanceStash.TabLoaded(0, inventory));
            await Game.SendStashTab(player, 1, InstanceStash.Personal);
            wire.Expect(InstanceInventories.AddInventory(stash.InventoryOf(1), stash.Inventory(1).Slot, 0, stash.Inventory(1).Items));
            wire.Expect(InstanceStash.TabLoaded(1, stash.InventoryOf(1)));
            Assert.Contains(0, player.Connection.StashTabsSent);
            Assert.Contains(1, player.Connection.StashTabsSent);

            // A change to a tab not yet sent for this connection (e.g. an affinity deposit or hidden child) still loads before 0xbf.
            player.Connection.StashTabsSent.Remove(0);
            await Game.SplitStack(player, new InstanceItems.SplitStack(inventory, stack, 1, 3), "");
            var left = stash.Inventory(0).Items.Single();
            var held = items.Items(PlayerInventories.Cursor).Single();
            Assert.Equal((1, 1), (left.Item.StackSize, held.Item.StackSize));
            wire.Expect(InstanceInventories.AddInventory(inventory, stash.Inventory(0).Slot, 0, [left]));
            wire.Expect(InstanceStash.TabLoaded(0, inventory));
            wire.Expect(InstanceItems.InventoryChange(inventory, 1, [stack], [left]));
            wire.Expect(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor), 1, [], [held]));
            Assert.Contains(0, player.Connection.StashTabsSent);
        }
        finally
        {
            PlayerInventories.Forget(account);
            AccountStash.Forget(account);
        }
    }

    [Fact]
    public async Task Tab_affinities_save_to_the_stash_their_kind_names_whichever_panel_is_open()
    {
        var catalog = new ItemCatalog("data");
        TestEnvironment.OpenOwnSaves("stashaffinities");
        string account = "Affinities" + Guid.NewGuid().ToString("N");
        string personalKey = AccountStash.Key(account, "Standard"), guildKey = "guild:" + personalKey;
        var player = new Player(new(ushort.MaxValue, "AffinitiesTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = { Account = account, Name = account, League = "Standard" }, GuildStashOpen = true };
        using var wire = new Wire();
        player.Connection.Session = new("stash-test", wire.Stream, wire.Ciphers);
        try
        {
            var personal = Game.PersonalStashOf(player);
            var guild = Game.GuildStashOf(player);
            uint Bits(AccountStash s) => s.Records(null, new HashSet<int>())[0].Affinities;

            // The guild panel is open: a guild change goes to the guild stash and its list goes out as kind 1.
            await Game.SetTabAffinities(player, new(0, InstanceStash.Guild, [(3, true)]));
            Assert.Equal(1u << 3, Bits(guild));
            Assert.Equal(0u, Bits(personal));
            wire.Expect(InstanceStash.TabList(guild.Records(null, player.Connection.GuildStashTabsSent), InstanceStash.Guild));

            // A personal change with the guild panel still open goes to the personal stash, not the guild's.
            await Game.SetTabAffinities(player, new(0, InstanceStash.Personal, [(5, true)]));
            Assert.Equal(1u << 5, Bits(personal));
            Assert.Equal(1u << 3, Bits(guild));
            wire.Expect(InstanceStash.TabList(personal.Records(null, player.Connection.StashTabsSent)));

            // Both are kept after a reload.
            AccountStash.Forget(personalKey);
            AccountStash.Forget(guildKey);
            Assert.Equal(1u << 5, Bits(Game.PersonalStashOf(player)));
            Assert.Equal(1u << 3, Bits(Game.GuildStashOf(player)));
        }
        finally
        {
            AccountStash.Forget(personalKey);
            AccountStash.Forget(guildKey);
        }
    }

    [Fact]
    public async Task Unique_collection_categories_remain_registered_after_another_tab_loads()
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
            var player = new Player(new(ushort.MaxValue, "UniqueReopenTest", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = { Account = account, Name = account, League = "Standard" },
                StashForcedTab = parent,
                StashForcedUntil = double.PositiveInfinity
            };
            using var wire = new Wire();
            player.Connection.Session = new("stash-test", wire.Stream, wire.Ciphers);

            // Load tabs 0, 1, currency, one map page, and the Unique collection with its hidden categories.
            foreach (ushort tab in new ushort[] { 0, 1, currencyTab, mapChild, parent })
            {
                await Game.SendStashTab(player, tab, InstanceStash.Personal);
                ExpectLoaded(wire, stash, tab, register: true);
                if (tab == parent)
                    foreach (int child in children) ExpectLoaded(wire, stash, child, register: true);
            }
            Assert.Equal(parent, stash.LastTab);
            Assert.Contains(0, player.Connection.StashTabsSent);
            Assert.Contains(1, player.Connection.StashTabsSent);

            var reopen = stash.Records(null, player.Connection.StashTabsSent, catalog);
            Assert.False(reopen[1].Loaded);
            Assert.Equal(0u, reopen[1].Inventory);
            foreach (int tab in new int[] { 0, currencyTab, parent, mapChild }.Concat(children))
            {
                Assert.True(reopen[tab].Loaded);
                Assert.Equal(stash.InventoryOf(tab), reopen[tab].Inventory);
            }

            // Replaying the parent's request after the first-open window keeps all visited tabs and categories registered.
            player.StashForcedUntil = double.NegativeInfinity;
            await Game.SendStashTab(player, parent, InstanceStash.Personal);
            ExpectLoaded(wire, stash, parent, register: false);
            foreach (int child in children) ExpectLoaded(wire, stash, child, register: false);
            foreach (var (tab, payment) in payments)
            {
                Assert.Contains(tab, player.Connection.StashTabsSent);
                Assert.Equal(payment, stash.Inventory(tab).Items.Single());
                Assert.True(Game.PreloadsForPrices(stash.TabType(tab), stash.Inventory(tab).Items, catalog));
            }
            Assert.Contains(1, player.Connection.StashTabsSent);
            Assert.Contains((int)mapChild, player.Connection.StashTabsSent);
            foreach (var row in stash.Records(null, player.Connection.StashTabsSent).Where(r => inventories.ContainsKey(r.Index)))
            {
                Assert.True(row.Loaded);
                Assert.Equal(inventories[row.Index], row.Inventory);
            }

            // The native panel can receive a delayed request for its old tab while still displaying Unique.
            await Game.SendStashTab(player, 1, InstanceStash.Personal);
            ExpectLoaded(wire, stash, 1, register: false);
            Assert.True(player.Connection.StashTabsSent.SetEquals([0, 1, currencyTab, mapChild, parent, .. children]));
            var afterOtherTab = stash.Records(null, player.Connection.StashTabsSent);
            Assert.All(children, child =>
            {
                var row = afterOtherTab[child];
                Assert.True(row.Loaded);
                Assert.Equal(inventories[child], row.Inventory);
            });
            await Game.SendStashTab(player, (ushort)children[0], InstanceStash.Personal);
            ExpectLoaded(wire, stash, children[0], register: false);
            Assert.Equal(4, stash.CountOf(currency.Hash));

            // A payment can still change both retained inventories directly, without another C0/C8 registration.
            var paid = stash.Pay(currency.Hash, 4);
            Assert.NotNull(paid);
            Assert.Equal(payments.Keys.Order(), paid.Tabs.Keys.Order());
            foreach (var (tab, payment) in payments)
            {
                Assert.Equal(new[] { payment.Id }, paid.Tabs[tab].Removed);
                Assert.Empty(paid.Tabs[tab].Changed);
                await Game.SendTabChange(player, stash, tab,
                    paid.Tabs[tab].Removed, paid.Tabs[tab].Changed, null);
                wire.Expect(InstanceItems.InventoryChange(stash.InventoryOf(tab), 1, [payment.Id], []));
            }
            Assert.Equal(0, stash.CountOf(currency.Hash));
            Assert.True(player.Connection.StashTabsSent.SetEquals([0, 1, currencyTab, mapChild, parent, .. children]));
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
            var player = new Player(new(ushort.MaxValue, "UniqueRefreshTest", 128, 128, 32, 32, null, []), 0, 1)
                { Character = { Account = account, Name = account, League = "Standard" } };
            using var wire = new Wire();
            player.Connection.Session = new("stash-test", wire.Stream, wire.Ciphers);
            // Establish the known inventory by the actual child request, not by seeding StashTabsSent.
            await Game.SendStashTab(player, (ushort)knownChild, InstanceStash.Personal);
            ExpectLoaded(wire, stash, knownChild, register: true);
            Assert.True(player.Connection.StashTabsSent.SetEquals([knownChild]));

            // UniqueStashFillTests covers none/all loaded. Here the real request leaves a partially loaded family.
            var before = stash.Records(null, player.Connection.StashTabsSent);
            foreach (int child in children)
            {
                Assert.NotEqual(0ul, before[child].HiddenKey);
                Assert.Equal(parent, before[child].Parent);
                Assert.Equal(child == knownChild, before[child].Loaded);
                Assert.Equal(child == knownChild ? stash.InventoryOf(child) : 0u, before[child].Inventory);
            }
            Assert.All(before.Where(r => r.HiddenKey != 0 && r.Inventory != 0), r => Assert.Equal(knownChild, r.Index));
            await Game.RefreshUniqueCollection(player, stash, parent);
            wire.Expect(InstanceStash.TabList(before));
            foreach (int child in children)
            {
                if (child == knownChild) wire.Expect(InstanceInventories.RemoveInventory(stash.InventoryOf(child)));
                ExpectLoaded(wire, stash, child, register: true);
            }
            Assert.True(player.Connection.StashTabsSent.SetEquals(children));

            // An explicit refresh of an entirely known family must still remove each child before C0/C8.
            // The reopen-retention rule must not turn this deliberate refresh into a no-op.
            before = stash.Records(null, player.Connection.StashTabsSent);
            Assert.All(children, child =>
            {
                Assert.True(before[child].Loaded);
                Assert.Equal(stash.InventoryOf(child), before[child].Inventory);
            });
            await Game.RefreshUniqueCollection(player, stash, parent);
            wire.Expect(InstanceStash.TabList(before));
            foreach (int child in children)
            {
                wire.Expect(InstanceInventories.RemoveInventory(stash.InventoryOf(child)));
                ExpectLoaded(wire, stash, child, register: true);
            }
            Assert.True(player.Connection.StashTabsSent.SetEquals(children));
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

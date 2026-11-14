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

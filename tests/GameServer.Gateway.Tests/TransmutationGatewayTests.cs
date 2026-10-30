using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class TransmutationGatewayTests
{
    const byte Main = PlayerInventories.MainInventory;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Ordinary_and_shift_use_spend_one_persist_and_refuse_a_second_upgrade(
        bool currencyInStash, bool targetInStash, bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currencyType = Program.Items.Find(CurrencyEffects.OrbOfTransmutation)!;
            var currency = Put(items, stash, currencyInStash, ItemCatalog.Create(currencyType) with { StackSize = 2 }, 0);
            var target = Put(items, stash, targetInStash, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            { ItemLevel = 84, Quality = 17 }, 4);
            var request = Request(stash, currency, target, currencyInStash, targetInStash, shift);
            await send(request);
            var after = Current(items, stash, targetInStash, target.Id);
            Assert.Equal(ItemRarity.Magic, after.Item.Rarity);
            Assert.True(after.Item.Identified);
            Assert.InRange(after.Item.Mods.Count(m => m.Section == ModSection.Explicit), 1, 2);
            Assert.Equal(17, after.Item.Quality);
            Assert.Equal(target.Item.Mods, after.Item.Mods.Where(m => m.Section != ModSection.Explicit));
            Assert.Equal(1, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);
            await send(request);
            Assert.Equal(after, Current(items, stash, targetInStash, target.Id));
            Assert.Equal(1, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);
            // Forget both runtime caches: assert the database, not merely the returned objects.
            PlayerInventories.Forget(conn.Character!);
            AccountStash.Forget(conn.Account!);
            var reloadedItems = PlayerInventories.For(conn.Character!, Program.Items);
            var reloadedStash = AccountStash.For(conn.Account!, Program.Items);
            var saved = Current(reloadedItems, reloadedStash, targetInStash, target.Id).Item;
            Assert.Equal(ItemRarity.Magic, saved.Rarity);
            Assert.Equal(after.Item.Mods.Select(m => (m.Hash, string.Join(',', m.Values))),
                saved.Mods.Select(m => (m.Hash, string.Join(',', m.Values))));
            Assert.Equal(1, Current(reloadedItems, reloadedStash, currencyInStash, currency.Id).Item.StackSize);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unmodifiable_targets_are_refused_without_spending_even_through_shift(bool currencyInStash, bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, currencyInStash,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfTransmutation)!) with { StackSize = 2 }, 0);
            var blocked = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
            Assert.True(blocked.Unmodifiable);
            var target = Put(items, stash, false, ItemCatalog.Create(blocked) with { ItemLevel = 84 }, 4);
            await send(Request(stash, currency, target, currencyInStash, false, shift));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);
        });
    }

    static async Task WithSession(Func<ClientState, PlayerInventories, AccountStash,
        Func<byte[], Task>, Task> action)
    {
        TestSaves.Ensure();
        string name = "Transmute" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(ushort.MaxValue, "TransmutationTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, Account = name, League = "Standard" };
        var items = PlayerInventories.For(name, Program.Items);
        var stash = AccountStash.For(name, Program.Items);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        try
        {
            Task Send(byte[] packet) => Program.OnClientMessages("transmutation-test", writer.GetStream(), ciphers,
                [((ushort)(packet[0] << 8 | packet[1]), packet)], [], conn).WaitAsync(TimeSpan.FromSeconds(5));
            await action(conn, items, stash, Send);
        }
        finally
        {
            PlayerInventories.Forget(name);
            AccountStash.Forget(name);
        }
    }

    static InventoryItem Put(PlayerInventories items, AccountStash stash, bool inStash, Item item, byte x) =>
        inStash ? stash.Place(0, x, 0, new InventoryItem(1, 0, 0, item), Shape, out _)!.Value.Placed
            : items.Add(Main, x, 0, item);

    static PlayerInventories.Shape? Shape(Item item) => Program.Items.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    static InventoryItem Current(PlayerInventories items, AccountStash stash, bool inStash, uint id) =>
        (inStash ? stash.Inventory(0).Items : items.Items(Main)).Single(i => i.Id == id);

    static byte[] Request(AccountStash stash, InventoryItem currency, InventoryItem target,
        bool currencyInStash, bool targetInStash, bool shift)
    {
        uint targetInventory = targetInStash ? stash.InventoryOf(0) : PlayerInventories.InventoryId(Main);
        return shift ? new LoginWriter().U16(0x72).U32(currency.Item.TypeHash).U32(targetInventory).U32(target.Id)
                .Bytes(new byte[9]).ToArray()
            : new LoginWriter().U16(0x71).U32(currencyInStash ? stash.InventoryOf(0) : PlayerInventories.InventoryId(Main))
                .U32(currency.Id).U32(targetInventory).U32(target.Id).ToArray();
    }
}

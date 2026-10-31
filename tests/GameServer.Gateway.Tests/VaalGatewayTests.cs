using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class VaalGatewayTests
{
    const byte Main = PlayerInventories.MainInventory;

    static ItemMod Explicit(string id) =>
        Program.Items.Mods.All.Single(m => m.Id == id).Roll(ModSection.Explicit, new Random(1));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Ordinary_and_shift_use_corrupt_spend_one_persist_and_refuse_a_second_use(
        bool currencyInStash, bool targetInStash, bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, currencyInStash,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.VaalOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, targetInStash,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84, Quality = 17 }, 4);
            var request = Request(stash, currency, target, currencyInStash, targetInStash, shift);

            await send(request);

            var after = Current(items, stash, targetInStash, target.Id);
            Assert.Equal(ItemRarity.Rare, after.Item.Rarity);
            Assert.True(after.Item.Corrupted);
            Assert.True(after.Item.Identified);
            Assert.InRange(after.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
            Assert.Equal(2, after.Item.NameWords.Count);
            Assert.Equal(17, after.Item.Quality);
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
            Assert.Equal(ItemRarity.Rare, saved.Rarity);
            Assert.True(saved.Corrupted);
            Assert.Equal(after.Item.Mods.Select(m => (m.Hash, string.Join(',', m.Values))),
                saved.Mods.Select(m => (m.Hash, string.Join(',', m.Values))));
            Assert.Equal(1, Current(reloadedItems, reloadedStash, currencyInStash, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_rare_target_is_corrupted()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.VaalOrb)!) with { StackSize = 2 }, 0);
            var rare = Program.Rolls.Roll(ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84 },
                ItemRarity.Rare, new Random(5), shape: (6, 3));
            var target = Put(items, stash, false, rare, 4);
            await send(Request(stash, currency, target, false, false, false));
            var after = Current(items, stash, false, target.Id);
            Assert.Equal(ItemRarity.Rare, after.Item.Rarity);
            Assert.True(after.Item.Corrupted);
            Assert.InRange(after.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
            Assert.Equal(1, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_corrupted_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.VaalOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84, Corrupted = true }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_unique_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.VaalOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84, Rarity = ItemRarity.Unique }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_mirrored_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.VaalOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84, Mirrored = true }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_jewel_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.VaalOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelStr")!) with { ItemLevel = 84 }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    static async Task WithSession(Func<ClientState, PlayerInventories, AccountStash,
        Func<byte[], Task>, Task> action)
    {
        TestSaves.Ensure();
        string name = "Vaal" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(ushort.MaxValue, "VaalTest", 128, 128, 32, 32, null, []), 0, 1)
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
            Task Send(byte[] packet) => Program.OnClientMessages("vaal-test", writer.GetStream(), ciphers,
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

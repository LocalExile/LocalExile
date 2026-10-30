using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class EldritchIchorGatewayTests
{
    const byte Main = PlayerInventories.MainInventory;

    static Item Helmet(ItemRarity rarity = ItemRarity.Rare) =>
        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStrDexRitual1")!) with
        {
            ItemLevel = 84,
            Rarity = rarity,
        };

    static bool IsEater(ItemMod mod, out ModCatalog.Mod? row)
    {
        row = Program.Items.Mods.ByHash(mod.Hash);
        return mod.Section == ModSection.Implicit && row?.GenerationType == ItemRolls.EaterImplicit;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Ordinary_and_shift_use_add_and_replace_one_eater_implicit_and_persist(
        bool currencyInStash, bool targetInStash, bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, currencyInStash,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!) with { StackSize = 3 }, 0);
            var target = Put(items, stash, targetInStash, Helmet(), 4);
            var request = Request(stash, currency, target, currencyInStash, targetInStash, shift);

            await send(request);
            var afterFirst = Current(items, stash, targetInStash, target.Id);
            var firstEater = Assert.Single(afterFirst.Item.Mods, m => IsEater(m, out _));
            Assert.EndsWith("1", Program.Items.Mods.ByHash(firstEater.Hash)!.Id);
            Assert.DoesNotContain(afterFirst.Item.Mods, m => m.Section == ModSection.Implicit && !IsEater(m, out _));
            Assert.Equal(2, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);

            await send(request);
            var after = Current(items, stash, targetInStash, target.Id);
            Assert.Single(after.Item.Mods, m => IsEater(m, out _));
            Assert.Equal(1, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);

            // Forget both runtime caches: assert the database, not merely the returned objects.
            PlayerInventories.Forget(conn.Character!);
            AccountStash.Forget(conn.Account!);
            var reloadedItems = PlayerInventories.For(conn.Character!, Program.Items);
            var reloadedStash = AccountStash.For(conn.Account!, Program.Items);
            var saved = Current(reloadedItems, reloadedStash, targetInStash, target.Id).Item;
            Assert.Equal(ItemRarity.Rare, saved.Rarity);
            Assert.Single(saved.Mods, m => IsEater(m, out _));
            Assert.Equal(after.Item.Mods.Select(m => (m.Hash, string.Join(',', m.Values))),
                saved.Mods.Select(m => (m.Hash, string.Join(',', m.Values))));
            Assert.Equal(1, Current(reloadedItems, reloadedStash, currencyInStash, currency.Id).Item.StackSize);
        });
    }

    [Theory]
    [InlineData(CurrencyEffects.LesserEldritchIchor, "1")]
    [InlineData(CurrencyEffects.GreaterEldritchIchor, "2")]
    [InlineData(CurrencyEffects.GrandEldritchIchor, "3")]
    [InlineData(CurrencyEffects.ExceptionalEldritchIchor, "4")]
    public async Task Each_ichor_adds_its_own_tier(string currencyId, string suffix)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(currencyId)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, Helmet(), 4);
            await send(Request(stash, currency, target, false, false, false));
            var after = Current(items, stash, false, target.Id);
            var eater = Assert.Single(after.Item.Mods, m => IsEater(m, out _));
            Assert.EndsWith(suffix, Program.Items.Mods.ByHash(eater.Hash)!.Id);
            Assert.Equal(1, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_non_armour_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring2")!) with { ItemLevel = 84 }, 4);
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
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, Helmet(ItemRarity.Unique), 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_corrupted_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, Helmet() with { Corrupted = true }, 4);
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
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, Helmet() with { Mirrored = true }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    static async Task WithSession(Func<ClientState, PlayerInventories, AccountStash,
        Func<byte[], Task>, Task> action)
    {
        TestSaves.Ensure();
        string name = "Ichor" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(ushort.MaxValue, "EldritchIchorTest", 128, 128, 32, 32, null, []), 0, 1)
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
            Task Send(byte[] packet) => Program.OnClientMessages("eldritch-ichor-test", writer.GetStream(), ciphers,
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

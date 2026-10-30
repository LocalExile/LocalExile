using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class MirrorGatewayTests
{
    const byte Main = PlayerInventories.MainInventory;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mirror_use_puts_a_mirrored_copy_on_the_cursor_and_spends_one(bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.MirrorOfKalandra)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, FourModWand() with { Quality = 17 }, 4);

            await send(Request(stash, currency, target, false, false, shift));

            Assert.Equal(1, Current(items, stash, false, currency.Id).Item.StackSize);
            Assert.Equal(target, Current(items, stash, false, target.Id));
            var copy = items.Items(PlayerInventories.Cursor).Single();
            Assert.True(copy.Item.Mirrored);
            Assert.Equal(target.Item.TypeHash, copy.Item.TypeHash);
            Assert.Equal(target.Item.Mods.Select(m => (m.Section, m.Hash, string.Join(',', m.Values))),
                copy.Item.Mods.Select(m => (m.Section, m.Hash, string.Join(',', m.Values))));
            Assert.Equal(target.Item.NameWords, copy.Item.NameWords);
            Assert.Equal(17, copy.Item.Quality);

            // The cursor is occupied: a second mirror is refused without spending.
            await send(Request(stash, currency, target, false, false, shift));
            Assert.Equal(1, Current(items, stash, false, currency.Id).Item.StackSize);
            Assert.Single(items.Items(PlayerInventories.Cursor));

            PlayerInventories.Forget(conn.Character!);
            var reloaded = PlayerInventories.For(conn.Character!, Program.Items);
            var savedCopy = reloaded.Items(PlayerInventories.Cursor).Single();
            Assert.True(savedCopy.Item.Mirrored);
            Assert.Equal(target.Item.Mods.Select(m => (m.Section, m.Hash, string.Join(',', m.Values))),
                savedCopy.Item.Mods.Select(m => (m.Section, m.Hash, string.Join(',', m.Values))));
            Assert.Equal(1, reloaded.Items(Main).Single(i => i.Id == currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_unique_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.MirrorOfKalandra)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, FourModWand() with { Rarity = ItemRarity.Unique }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
            Assert.Empty(items.Items(PlayerInventories.Cursor));
        });
    }

    [Theory]
    [InlineData("Metadata/Items/Flasks/FlaskUtility1")]
    [InlineData("Metadata/Items/Jewels/JewelStr")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    public async Task A_non_equipment_target_is_refused_without_spending(string id)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.MirrorOfKalandra)!) with { StackSize = 2 }, 0);
            var type = Program.Items.Find(id)!;
            var target = Put(items, stash, false, Rare(type, Explicit("LocalIncreasedPhysicalDamagePercent1"),
                Explicit("IncreasedManaWeapon1"), Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")), 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
            Assert.Empty(items.Items(PlayerInventories.Cursor));
        });
    }

    [Fact]
    public async Task A_corrupted_or_mirrored_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.MirrorOfKalandra)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, FourModWand() with { Corrupted = true, Mirrored = true }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
            Assert.Empty(items.Items(PlayerInventories.Cursor));
        });
    }

    [Fact]
    public async Task A_mirrored_item_takes_no_currency_even_a_scroll_of_wisdom()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var wisdom = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyIdentification")!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false, FourModWand() with { Mirrored = true, Identified = false }, 4);
            await send(Request(stash, wisdom, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, wisdom.Id).Item.StackSize);
        });
    }

    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Rare(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item FourModWand() => Rare(Wand,
        Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
        Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")) with { ItemLevel = 1 };

    static async Task WithSession(Func<ClientState, PlayerInventories, AccountStash,
        Func<byte[], Task>, Task> action)
    {
        TestSaves.Ensure();
        string name = "Mirror" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(ushort.MaxValue, "MirrorTest", 128, 128, 32, 32, null, []), 0, 1)
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
            Task Send(byte[] packet) => Program.OnClientMessages("mirror-test", writer.GetStream(), ciphers,
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

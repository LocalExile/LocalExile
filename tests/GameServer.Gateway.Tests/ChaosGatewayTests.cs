using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class ChaosGatewayTests
{
    const byte Main = PlayerInventories.MainInventory;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Rare(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item FourModWand() => Rare(Program.Items.Find("Driftwood Wand")!,
        Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
        Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")) with { ItemLevel = 84 };

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Ordinary_and_shift_use_reroll_spend_one_and_persist(
        bool currencyInStash, bool targetInStash, bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, currencyInStash,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.ChaosOrb)!) with { StackSize = 3 }, 0);
            var target = Put(items, stash, targetInStash, FourModWand() with { Quality = 17 }, 4);
            var request = Request(stash, currency, target, currencyInStash, targetInStash, shift);

            await send(request);

            var afterFirst = Current(items, stash, targetInStash, target.Id);
            Assert.Equal(ItemRarity.Rare, afterFirst.Item.Rarity);
            Assert.True(afterFirst.Item.Identified);
            Assert.InRange(afterFirst.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
            Assert.Equal(2, afterFirst.Item.NameWords.Count);
            Assert.Equal(17, afterFirst.Item.Quality);
            Assert.Equal(2, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);

            await send(request);
            var after = Current(items, stash, targetInStash, target.Id);
            Assert.Equal(ItemRarity.Rare, after.Item.Rarity);
            Assert.InRange(after.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
            Assert.Equal(1, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);

            // Forget both runtime caches: assert the database, not merely the returned objects.
            PlayerInventories.Forget(conn.Character!);
            AccountStash.Forget(conn.Account!);
            var reloadedItems = PlayerInventories.For(conn.Character!, Program.Items);
            var reloadedStash = AccountStash.For(conn.Account!, Program.Items);
            var saved = Current(reloadedItems, reloadedStash, targetInStash, target.Id).Item;
            Assert.Equal(ItemRarity.Rare, saved.Rarity);
            Assert.Equal(after.Item.NameWords, saved.NameWords);
            Assert.Equal(after.Item.Mods.Select(m => (m.Hash, string.Join(',', m.Values))),
                saved.Mods.Select(m => (m.Hash, string.Join(',', m.Values))));
            Assert.Equal(1, Current(reloadedItems, reloadedStash, currencyInStash, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_full_rare_item_is_rerolled()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.ChaosOrb)!) with { StackSize = 2 }, 0);
            var full = Program.Rolls.Roll(ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84 },
                ItemRarity.Rare, new Random(9), shape: (6, 3));
            var target = Put(items, stash, false, full, 4);
            await send(Request(stash, currency, target, false, false, false));
            var after = Current(items, stash, false, target.Id);
            Assert.Equal(ItemRarity.Rare, after.Item.Rarity);
            Assert.InRange(after.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
            Assert.Equal(1, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_magic_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.ChaosOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84, Rarity = ItemRarity.Magic }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_rare_flask_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.ChaosOrb)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                Rare(Program.Items.Find("Small Life Flask")!, Explicit("FlaskIncreasedRecoverySpeed1")), 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unmodifiable_targets_are_refused_without_spending_even_through_shift(bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.ChaosOrb)!) with { StackSize = 2 }, 0);
            var blocked = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
            Assert.True(blocked.Unmodifiable);
            var prefix = Program.Items.Mods.All.First(m => m.Domain == blocked.ModDomain &&
                m.GenerationType == ItemRolls.Prefix && m.Level <= 84);
            var target = Put(items, stash, false,
                Rare(blocked, prefix.Roll(ModSection.Explicit, new Random(1))) with { ItemLevel = 84 }, 4);
            await send(Request(stash, currency, target, false, false, shift));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    static async Task WithSession(Func<ClientState, PlayerInventories, AccountStash,
        Func<byte[], Task>, Task> action)
    {
        TestSaves.Ensure();
        string name = "Chaos" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(ushort.MaxValue, "ChaosTest", 128, 128, 32, 32, null, []), 0, 1)
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
            Task Send(byte[] packet) => Program.OnClientMessages("chaos-test", writer.GetStream(), ciphers,
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

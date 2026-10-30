using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class AnnulmentGatewayTests
{
    const byte Main = PlayerInventories.MainInventory;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Magic(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Magic,
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item Rare(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Ordinary_and_shift_use_remove_one_spend_one_and_persist(
        bool currencyInStash, bool targetInStash, bool shift)
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, currencyInStash,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 3 }, 0);
            var target = Put(items, stash, targetInStash,
                Magic(Program.Items.Find("Driftwood Wand")!,
                    Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1")), 4);
            var request = Request(stash, currency, target, currencyInStash, targetInStash, shift);

            await send(request);
            var afterFirst = Current(items, stash, targetInStash, target.Id);
            Assert.Equal(ItemRarity.Magic, afterFirst.Item.Rarity);
            Assert.Single(afterFirst.Item.Mods, m => m.Section == ModSection.Explicit);
            Assert.Equal(2, Current(items, stash, currencyInStash, currency.Id).Item.StackSize);

            await send(request);
            var after = Current(items, stash, targetInStash, target.Id);
            Assert.Equal(ItemRarity.Magic, after.Item.Rarity);
            Assert.DoesNotContain(after.Item.Mods, m => m.Section == ModSection.Explicit);
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
            Assert.DoesNotContain(saved.Mods, m => m.Section == ModSection.Explicit);
            Assert.Equal(1, Current(reloadedItems, reloadedStash, currencyInStash, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_rare_item_loses_one_affix_and_keeps_its_name()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                Rare(Program.Items.Find("Driftwood Wand")!,
                    Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
                    Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")), 4);
            await send(Request(stash, currency, target, false, false, false));
            var after = Current(items, stash, false, target.Id);
            Assert.Equal(ItemRarity.Rare, after.Item.Rarity);
            Assert.Equal(new uint[] { 111, 222 }, after.Item.NameWords.ToArray());
            Assert.Equal(3, after.Item.Mods.Count(m => m.Section == ModSection.Explicit));
            Assert.Equal(1, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task A_normal_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { ItemLevel = 84 }, 4);
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
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                Magic(Program.Items.Find("Driftwood Wand")!, Explicit("LocalIncreasedPhysicalDamagePercent1"))
                    with { Corrupted = true }, 4);
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
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 2 }, 0);
            var target = Put(items, stash, false,
                Magic(Program.Items.Find("Driftwood Wand")!, Explicit("LocalIncreasedPhysicalDamagePercent1"))
                    with { Mirrored = true }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task An_unmodifiable_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 2 }, 0);
            var blocked = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
            Assert.True(blocked.Unmodifiable);
            var prefix = Program.Items.Mods.All.First(m => m.Domain == blocked.ModDomain &&
                m.GenerationType == ItemRolls.Prefix && m.Level <= 84);
            var target = Put(items, stash, false,
                Rare(blocked, prefix.Roll(ModSection.Explicit, new Random(1))) with { ItemLevel = 84 }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    [Fact]
    public async Task An_unsupported_target_is_refused_without_spending()
    {
        await WithSession(async (conn, items, stash, send) =>
        {
            var currency = Put(items, stash, false,
                ItemCatalog.Create(Program.Items.Find(CurrencyEffects.OrbOfAnnulment)!) with { StackSize = 2 }, 0);
            var type = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/StormBladeOneHand")!;
            var mod = Program.Rolls.Pool(type, 84).First().Mod.Roll(ModSection.Explicit, new Random(1));
            var target = Put(items, stash, false, Magic(type, mod) with { ItemLevel = 84 }, 4);
            await send(Request(stash, currency, target, false, false, false));
            Assert.Equal(target, Current(items, stash, false, target.Id));
            Assert.Equal(2, Current(items, stash, false, currency.Id).Item.StackSize);
        });
    }

    static async Task WithSession(Func<ClientState, PlayerInventories, AccountStash,
        Func<byte[], Task>, Task> action)
    {
        TestSaves.Ensure();
        string name = "Annul" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(ushort.MaxValue, "AnnulmentTest", 128, 128, 32, 32, null, []), 0, 1)
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
            Task Send(byte[] packet) => Program.OnClientMessages("annulment-test", writer.GetStream(), ciphers,
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

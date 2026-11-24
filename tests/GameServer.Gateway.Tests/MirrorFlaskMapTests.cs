using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MirrorFlaskMapTests
{
    static void Mirror_admits_flasks_and_actual_tier_1_to_16_maps_preserving_every_property()
    {
        var catalog = Program.Items;
        var rolls = new ItemRolls(catalog);
        var atlas = new AtlasCatalog(catalog);
        var device = MapDevice.Load("data", row => catalog.Row(row)?.Hash, catalog);
        var flasks = catalog.All.Where(t => t.Supported && !t.Unmodifiable && t.Components.Contains("Mods") && t.Components.Contains("Flask")).ToArray();
        Assert.NotEmpty(flasks);
        var maps = catalog.All.Where(t => t.Supported && !t.Unmodifiable && t.Components.Contains("Mods") && (t.Components.Contains("MapKey") || t.Components.Contains("Map"))
            && (atlas.TierOf(t.Hash) ?? device.AreaOf(t.Hash)?.Tier) is >= 1 and <= 16).ToArray();
        Assert.NotEmpty(maps);
        for (int tier = 1; tier <= 16; tier++) Assert.Contains(maps, t => atlas.TierOf(t.Hash) == tier);
        foreach (var type in flasks.Concat(maps))
        {
            var source = ItemCatalog.Create(type) with { ItemLevel = 83, Quality = 20,
                Rarity = ItemRarity.Magic, Identified = true, NameWords = [111u, 222u],
                Mods = [new(ModSection.Explicit, 123, [17, 29])], FracturedCount = 1 };
            foreach (var rarity in new[] { ItemRarity.Normal, ItemRarity.Magic, ItemRarity.Rare })
            {
                var original = source with { Rarity = rarity };
                var copy = CurrencyEffects.Mirror(original, rolls);
                Assert.True(copy is not null, $"Mirror refused {type.Id}; unmodifiable={type.Unmodifiable}, Mods={type.Components.Contains("Mods")}");
                Assert.Equal(original with { Mirrored = true }, copy);
                Assert.False(original.Mirrored);
            }
            Assert.Null(rolls.Mirror(source with { Rarity = ItemRarity.Unique }));
            Assert.Null(rolls.Mirror(source with { Corrupted = true }));
            Assert.Null(rolls.Mirror(source with { Mirrored = true }));
        }
        var tier17 = catalog.ByHash(atlas.KeyOfTier(17)!.Value)!;
        Assert.Null(rolls.Mirror(ItemCatalog.Create(tier17)));
        Assert.Null(rolls.Mirror(ItemCatalog.Create(catalog.Find("Metadata/Items/Maps/MapKeyNightmareBoss")!)));
        Assert.Null(rolls.Mirror(ItemCatalog.Create(catalog.Find("Metadata/Items/Jewels/JewelInt")!)));
        Assert.Null(rolls.Mirror(ItemCatalog.Create(catalog.Find(CurrencyEffects.MirrorOfKalandra)!)));
    }

    [Fact]
    public async Task Mirror_targets_and_transactions_preserve_items_and_persist_copies()
    {
        Mirror_admits_flasks_and_actual_tier_1_to_16_maps_preserving_every_property();
        string save = Path.Combine(Path.GetTempPath(), $"mirror-flask-map-{Guid.NewGuid():N}.db");
        Saves.Open(save);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string name = "MirrorFlaskMap" + Guid.NewGuid().ToString("N");
        var c = runtime.PlayerOf(new(), wire, name);
        var items = Game.ItemsOf(c);
        const byte main = PlayerInventories.MainInventory;
        uint inventory = PlayerInventories.InventoryId(main);
        try
        {
            var mirror = ItemCatalog.Create(Program.Items.Find(CurrencyEffects.MirrorOfKalandra)!) with { StackSize = 5 };
            var currency = items.Add(main, 0, 0, mirror);
            var atlas = new AtlasCatalog(Program.Items);
            var targets = new[] { Program.Items.All.First(t => t.Supported && t.Components.Contains("Flask")),
                Program.Items.ByHash(atlas.KeyOfTier(16)!.Value)! };
            for (int index = 0; index < targets.Length; index++)
            {
                var source = ItemCatalog.Create(targets[index]) with { Quality = 20, ItemLevel = 83 };
                var target = items.Add(main, 3, 0, source);
                byte[] packet = new byte[index == 0 ? 18 : 23];
                ushort id = index == 0 ? InstanceItems.UseItemOnId : InstanceItems.UseItemTypeOnId;
                BinaryPrimitives.WriteUInt16BigEndian(packet, id);
                if (index == 0)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), inventory);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), currency.Id);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10), inventory);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(14), target.Id);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), mirror.TypeHash);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), inventory);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10), target.Id);
                }
                await Game.OnClientMessages([(id, packet)], [], c);
                var held = Assert.Single(items.Items(PlayerInventories.Cursor));
                Assert.NotEqual(target.Id, held.Id);
                Assert.Equal(source with { Mirrored = true }, held.Item);
                Assert.Equal(source, items.Items(main).Single(i => i.Id == target.Id).Item);
                Assert.Equal(4 - index, items.Items(main).Single(i => i.Id == currency.Id).Item.StackSize);
                await Game.OnClientMessages([(id, packet)], [], c);
                Assert.Equal(4 - index, items.Items(main).Single(i => i.Id == currency.Id).Item.StackSize);
                Assert.Single(items.Items(PlayerInventories.Cursor));
                items.Remove(PlayerInventories.Cursor, held.Id);
                items.Add(main, 6, (byte)index, held.Item);
                items.Remove(main, target.Id);
            }
            PlayerInventories.Forget(name);
            var restored = PlayerInventories.For(name, Program.Items);
            Assert.Equal(2, restored.Items(main).Count(i => i.Item.Mirrored));
            Assert.Equal(3, restored.Items(main).Single(i => i.Item.TypeHash == mirror.TypeHash).Item.StackSize);
        }
        finally { PlayerInventories.Forget(name); AccountStash.Forget(name); TestEnvironment.ReleaseSaves(save); }
    }

    [Fact]
    public async Task Mirror_from_and_onto_stash_tabs_spends_one_and_holds_the_copy()
    {
        string save = Path.Combine(Path.GetTempPath(), $"mirror-stash-{Guid.NewGuid():N}.db");
        Saves.Open(save);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string name = "MirrorStash" + Guid.NewGuid().ToString("N");
        var c = runtime.PlayerOf(new(), wire, name);
        var items = Game.ItemsOf(c);
        const byte main = PlayerInventories.MainInventory;
        uint inventory = PlayerInventories.InventoryId(main);
        try
        {
            var stash = Game.StashOf(c);
            Assert.Same(stash, AccountStash.For(name, Program.Items));
            var tabs = stash.Records(null, new HashSet<int>()).Where(t => t.Type is 0 or 1 && !t.Hidden && t.HiddenKey == 0).Select(t => t.Index).ToArray();
            int mirrorTab = tabs[0], targetTab = tabs[1];
            var mirror = ItemCatalog.Create(Program.Items.Find(CurrencyEffects.MirrorOfKalandra)!) with { StackSize = 2 };
            var source = ItemCatalog.Create(Program.Items.All.First(t => t.Supported && t.Components.Contains("Flask"))) with { Quality = 20, ItemLevel = 83 };
            var tabMirror = stash.Place(mirrorTab, 11, 0, new InventoryItem(stash.NewItemId(), 0, 0, mirror), Game.ShapeOf, out _)!.Value.Placed;
            var invMirror = items.Add(main, 0, 0, mirror);
            var invFlask = items.Add(main, 3, 0, source);
            var tabFlask = stash.Place(targetTab, 0, 0, new InventoryItem(stash.NewItemId(), 0, 0, source), Game.ShapeOf, out _)!.Value.Placed;
            async Task<InventoryItem> Use(uint usedInventory, uint usedId, uint targetInventory, uint targetId, byte column)
            {
                byte[] packet = new byte[18];
                BinaryPrimitives.WriteUInt16BigEndian(packet, InstanceItems.UseItemOnId);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), usedInventory);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), usedId);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10), targetInventory);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(14), targetId);
                await Game.OnClientMessages([(InstanceItems.UseItemOnId, packet)], [], c);
                var held = Assert.Single(items.Items(PlayerInventories.Cursor));
                Assert.Equal(source with { Mirrored = true }, held.Item);
                Assert.True(held.Id < AccountStash.FirstItemId);
                // Holding the copy, a second use spends nothing.
                await Game.OnClientMessages([(InstanceItems.UseItemOnId, packet)], [], c);
                Assert.Single(items.Items(PlayerInventories.Cursor));
                items.Remove(PlayerInventories.Cursor, held.Id);
                items.Add(main, 6, column, held.Item);
                return held;
            }
            // A Mirror in a tab on a flask in the inventory: the tab is sent before its change.
            await Use(stash.InventoryOf(mirrorTab), tabMirror.Id, inventory, invFlask.Id, 0);
            Assert.Equal(1, stash.Inventory(mirrorTab).Items.Single(i => i.Id == tabMirror.Id).Item.StackSize);
            Assert.Equal(source, items.Items(main).Single(i => i.Id == invFlask.Id).Item);
            Assert.Contains(mirrorTab, c.Connection.StashTabsSent);
            // The tab's last Mirror on a flask in another tab: the stack goes, the target stays as it was.
            await Use(stash.InventoryOf(mirrorTab), tabMirror.Id, stash.InventoryOf(targetTab), tabFlask.Id, 1);
            Assert.DoesNotContain(stash.Inventory(mirrorTab).Items, i => i.Id == tabMirror.Id);
            Assert.Equal(source, stash.Inventory(targetTab).Items.Single(i => i.Id == tabFlask.Id).Item);
            // A Mirror in the inventory on the flask in the tab.
            await Use(inventory, invMirror.Id, stash.InventoryOf(targetTab), tabFlask.Id, 2);
            Assert.Equal(1, items.Items(main).Single(i => i.Id == invMirror.Id).Item.StackSize);
            PlayerInventories.Forget(name);
            AccountStash.Forget(name);
            var restored = PlayerInventories.For(name, Program.Items);
            var restoredStash = AccountStash.For(name, Program.Items);
            Assert.Equal(3, restored.Items(main).Count(i => i.Item.Mirrored));
            Assert.Equal(1, restored.Items(main).Single(i => i.Item.TypeHash == mirror.TypeHash).Item.StackSize);
            Assert.DoesNotContain(restoredStash.Inventory(mirrorTab).Items, i => i.Item.TypeHash == mirror.TypeHash);
            var restoredFlask = restoredStash.Inventory(targetTab).Items.Single(i => i.Id == tabFlask.Id).Item;
            Assert.False(restoredFlask.Mirrored);
            Assert.Equal((source.TypeHash, source.Quality, source.ItemLevel), (restoredFlask.TypeHash, restoredFlask.Quality, restoredFlask.ItemLevel));
        }
        finally { PlayerInventories.Forget(name); AccountStash.Forget(name); TestEnvironment.ReleaseSaves(save); }
    }
}

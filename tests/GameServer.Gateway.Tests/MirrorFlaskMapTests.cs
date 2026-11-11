using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MirrorFlaskMapTests
{
    [Fact]
    public void Mirror_admits_flasks_and_actual_tier_1_to_16_maps_preserving_every_property()
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
    public async Task Ordinary_and_repeat_mirroring_spend_one_leave_the_source_and_persist_the_copy()
    {
        string save = Path.Combine(Path.GetTempPath(), $"mirror-flask-map-{Guid.NewGuid():N}.db");
        Saves.Open(save);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string name = "MirrorFlaskMap" + Guid.NewGuid().ToString("N");
        var c = runtime.Connection(new(), wire, name);
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
                await Game.OnClientMessages("mirror-currency", wire.Stream, wire.Ciphers, [(id, packet)], [], c);
                var held = Assert.Single(items.Items(PlayerInventories.Cursor));
                Assert.NotEqual(target.Id, held.Id);
                Assert.Equal(source with { Mirrored = true }, held.Item);
                Assert.Equal(source, items.Items(main).Single(i => i.Id == target.Id).Item);
                Assert.Equal(4 - index, items.Items(main).Single(i => i.Id == currency.Id).Item.StackSize);
                await Game.OnClientMessages("mirror-occupied-cursor", wire.Stream, wire.Ciphers, [(id, packet)], [], c);
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
}

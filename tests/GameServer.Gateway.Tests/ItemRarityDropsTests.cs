using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The drop table's stages (ItemDrops): which bases may drop, the slot before the base, and the rarity last, raised by
// increased rarity of items found (Gold Flask, gear, passives) and a league's or mod's rarity multiplier.
public class ItemRarityDropsTests
{
    static readonly Lazy<(ItemCatalog Catalog, ItemDrops Drops)> Setup = new(() =>
    {
        string data = BenchCraftingTests.Data();
        var catalog = new ItemCatalog(data);
        return (catalog, new ItemDrops(catalog, new ItemRolls(catalog, data), data, uniques: new UniqueCatalog(catalog, data)));
    });

    static List<ItemDrops.Drop> Many(int level, MonsterRarity rarity, int seeds, double rarityMultiplier = 1, int increased = 0) =>
        [.. Enumerable.Range(0, seeds).SelectMany(seed => Setup.Value.Drops.Roll(level, rarity, new Random(seed), increased, rarityMultiplier: rarityMultiplier))];

    [Fact]
    public void Increased_rarity_raises_every_rarity_chance_uniques_too()
    {
        var normal = ItemDrops.RarityChances(MonsterRarity.Normal);
        Assert.Equal(normal, ItemDrops.WithIncreasedRarity(normal, 0));
        // The Gold Flask's 30%.
        var gold = ItemDrops.WithIncreasedRarity(normal, 30);
        Assert.Equal(normal.Unique * 1.3, gold.Unique, 9);
        Assert.Equal(normal.Rare * 1.3, gold.Rare, 9);
        Assert.Equal(normal.Magic * 1.3, gold.Magic, 9);
        // A multiplier on top, and each chance stops at 1.
        var huge = ItemDrops.WithIncreasedRarity(normal, 100, 1000);
        Assert.Equal(new ItemDrops.Chances(1, 1, 1), huge);
        // Reduced rarity lowers them.
        Assert.Equal(normal.Magic / 2, ItemDrops.WithIncreasedRarity(normal, -50).Magic, 9);
    }

    [Fact]
    public void Bases_the_game_keeps_out_of_the_drop_pools_never_drop()
    {
        var catalog = Setup.Value.Catalog;
        // Unique-only and special bases (not_for_sale): the "Ring" of a unique, Stygian Vise, a Heist base.
        foreach (string id in new[] { "Metadata/Items/Rings/MirrorRing", "Metadata/Items/Belts/BeltAbyss", "Metadata/Items/Weapons/OneHandWeapons/Wands/WandE1" })
            Assert.False(ItemDrops.Droppable(catalog.Find(id)!), id);
        // Ordinary bases drop, and so do the atlas bases (not_for_sale too, but they drop in maps).
        foreach (string id in new[] { "Metadata/Items/Rings/Ring1", "Metadata/Items/Belts/Belt3", "Metadata/Items/Rings/RingAtlas1", "Metadata/Items/Armours/Boots/BootsAtlas1" })
            Assert.True(ItemDrops.Droppable(catalog.Find(id)!), id);
        var dropped = Many(85, MonsterRarity.Unique, 600).Select(d => d.Type.Id).ToHashSet();
        Assert.DoesNotContain("Metadata/Items/Rings/MirrorRing", dropped);
        Assert.DoesNotContain("Metadata/Items/Belts/BeltAbyss", dropped);
    }

    [Fact]
    public void Armour_of_every_attribute_drops_alike()
    {
        var attributes = Many(70, MonsterRarity.Unique, 800).Where(d => d.Type.Class == "Body Armour")
            .GroupBy(d => System.Text.RegularExpressions.Regex.Match(d.Type.Id, "Body(Str|Dex|Int)+").Value)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(6, attributes.Count);
        Assert.True(attributes.Values.Min() * 2 > attributes.Values.Max(), string.Join(", ", attributes));
    }

    [Theory]
    [InlineData(MonsterRarity.Magic, 51)]
    [InlineData(MonsterRarity.Rare, 52)]
    [InlineData(MonsterRarity.Unique, 53)]
    public void A_monsters_rarity_raises_its_drops_item_level(MonsterRarity rarity, int itemLevel)
    {
        var equipment = Many(50, rarity, 200).Where(d => d.Item.Components.Contains("Armour")).ToList();
        Assert.NotEmpty(equipment);
        Assert.All(equipment, d => Assert.Equal(itemLevel, d.Item.ItemLevel));
        Assert.Equal(0, ItemDrops.ItemLevelBonus(MonsterRarity.Normal));
    }

    [Fact]
    public void A_uniques_weight_follows_its_disenchant_value()
    {
        var drops = Setup.Value.Drops;
        Assert.InRange(drops.UniqueWeight("Wanderlust") / drops.UniqueWeight("Mageblood"), 800, 900);
        var belts = GameDataCatalog.Uniques.Where(u => u.BaseTypeId == "Metadata/Items/Belts/Belt4").ToList();
        Assert.Contains(belts, u => u.Name == "Mageblood");
        var rng = new Random(1);
        var picks = Enumerable.Range(0, 200_000).Select(_ => drops.PickUnique(belts, rng)?.Name).ToList();
        foreach (var unique in belts)
        {
            double expected = drops.UniqueWeight(unique.Name) / Math.Max(1, belts.Sum(u => drops.UniqueWeight(u.Name)));
            Assert.InRange(picks.Count(p => p == unique.Name) / 200_000.0, expected * 0.5, expected * 1.5 + 0.0005);
        }
        // Alone on a base it stays rare: the rest of the range is no unique.
        var alone = GameDataCatalog.Uniques.Where(u => u.Name == "Mageblood").ToList();
        Assert.InRange(Enumerable.Range(0, 20_000).Count(_ => drops.PickUnique(alone, rng) is not null), 0, 100);
        // Old names outside the table do not drop.
        Assert.Equal(0, drops.UniqueWeight("Victario's Flight"));
    }

    [Fact]
    public void Armour_drops_far_more_often_than_jewellery()
    {
        var classes = Many(70, MonsterRarity.Unique, 800).GroupBy(d => d.Type.Class).ToDictionary(g => g.Key, g => g.Count());
        int armour = new[] { "Body Armour", "Helmet", "Gloves", "Boots" }.Sum(c => classes.GetValueOrDefault(c));
        int jewellery = new[] { "Ring", "Amulet" }.Sum(c => classes.GetValueOrDefault(c));
        Assert.True(jewellery > 0);
        Assert.InRange((double)armour / jewellery, 4, 20);
    }

    [Fact]
    public void Uniques_drop_on_their_own_base_and_rarity_scales_every_tier()
    {
        var catalog = Setup.Value.Catalog;
        var ordinary = Many(70, MonsterRarity.Rare, 400);
        var boosted = Many(70, MonsterRarity.Rare, 400, rarityMultiplier: 50);
        int Count(IEnumerable<ItemDrops.Drop> drops, ItemRarity rarity) => drops.Count(d => d.Item.Rarity == rarity);
        Assert.True(Count(boosted, ItemRarity.Unique) > 10 * Math.Max(1, Count(ordinary, ItemRarity.Unique)));
        var uniques = boosted.Where(d => d.Item.Rarity == ItemRarity.Unique).ToList();
        Assert.All(uniques, d =>
        {
            Assert.Equal(d.Type.Hash, d.Item.TypeHash);
            Assert.Contains(GameDataCatalog.Uniques, u => u.BaseTypeId == d.Type.Id);
            Assert.False(d.Item.Identified);
            Assert.Equal(72, d.Item.ItemLevel);
        });
        // A base no unique uses becomes rare instead.
        Assert.All(boosted.Where(d => d.Item.Rarity != ItemRarity.Unique && d.Item.Components.Contains("Mods")
                && catalog.Gem(d.Type) is null && !GameDataCatalog.Uniques.Any(u => u.BaseTypeId == d.Type.Id)
                && d.Type.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots"),
            d => Assert.Equal(ItemRarity.Rare, d.Item.Rarity));
        Assert.All(Many(70, MonsterRarity.Rare, 100, rarityMultiplier: 0), d => Assert.NotEqual(ItemRarity.Unique, d.Item.Rarity));
    }
}

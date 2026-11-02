using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class NessaShopTabsTests
{
    const string Nessa = "Metadata/NPC/Act1/Nessa";

    static List<NpcShops.ShopTab> Tabs(ItemCatalog catalog)
    {
        uint next = 1;
        return NpcShops.Tabs(Nessa, catalog, () => next++, [], fullGemStock: true);
    }

    static ItemCatalog.GemType Gem(ItemCatalog catalog, InventoryItem item) =>
        catalog.Gem(catalog.ByHash(item.Item.TypeHash)!)!;

    [Fact]
    public void Awakened_and_exceptional_gems_are_separate_from_the_colour_tabs()
    {
        var catalog = new ItemCatalog("data");
        var tabs = Tabs(catalog);

        var awakened = Assert.Single(tabs, t => t.Name == "Awakened Gems").Items;
        var exceptional = Assert.Single(tabs, t => t.Name == "Exceptional Gems").Items;
        Assert.NotEmpty(awakened);
        Assert.NotEmpty(exceptional);
        Assert.All(awakened, item => Assert.True(Gem(catalog, item).IsAwakened));
        Assert.All(exceptional, item => Assert.True(Gem(catalog, item).IsExceptional
            && !Gem(catalog, item).IsAwakened));
        Assert.Contains(awakened, item => catalog.GemName(item.Item).StartsWith("Awakened ", StringComparison.Ordinal));
        Assert.Contains(exceptional, item => catalog.GemName(item.Item).StartsWith("Greater ", StringComparison.Ordinal));

        var colourNames = new HashSet<string>(["Red Skills", "Red Support", "Green Skills", "Green Support",
            "Blue Skills", "Blue Support", "White Skills"]);
        var colour = tabs.Where(t => colourNames.Contains(t.Name)).SelectMany(t => t.Items).ToList();
        Assert.NotEmpty(colour);
        Assert.All(colour, item => Assert.False(Gem(catalog, item).IsAwakened));
        Assert.All(colour, item => Assert.False(Gem(catalog, item).IsExceptional));
        Assert.All(awakened.Concat(exceptional), item => Assert.NotEmpty(item.Item.Payload()));
        Assert.DoesNotContain(tabs.Where(t => t.Name.StartsWith("Goods")).SelectMany(t => t.Items),
            item => catalog.ByHash(item.Item.TypeHash)!.Components.Contains("SkillGem"));
        // A fresh character has no progression stock, so the future gameplay tab must not be an empty tab.
        Assert.DoesNotContain(tabs, t => t.Name == "Skills");
    }

    [Fact]
    public void Maps_use_generic_tier_keys_and_unique_maps_have_their_own_tab()
    {
        var catalog = new ItemCatalog("data");
        var tabs = Tabs(catalog);

        var maps = Assert.Single(tabs, t => t.Name == "Maps").Items;
        var uniques = Assert.Single(tabs, t => t.Name == "Unique Maps").Items;
        var originator = catalog.Mods.ById("MapZanaInfluenced");
        Assert.NotNull(originator);
        ushort originatorHash = originator!.Hash;
        bool Influenced(InventoryItem item) => item.Item.Mods.Any(mod =>
            mod.Section == ModSection.Implicit && mod.Hash == originatorHash);
        var keys = maps.Where(item => catalog.ByHash(item.Item.TypeHash) is { Class: "MapKey" } type
            && type.Name.StartsWith("Map (Tier ", StringComparison.Ordinal) && !Influenced(item)).ToList();
        Assert.Equal(16, keys.Count);
        Assert.Equal(Enumerable.Range(1, 16).Select(tier => $"Map (Tier {tier})"),
            keys.Select(item => catalog.ByHash(item.Item.TypeHash)!.Name));
        Assert.Equal(19, maps.Count);
        Assert.Equal(2, maps.Count(item => catalog.ByHash(item.Item.TypeHash)!.Name == "Nightmare Map"));
        Assert.DoesNotContain(maps, item => catalog.ByHash(item.Item.TypeHash)!.Name is
            "Sanctuary Map" or "Citadel Map" or "Fortress Map" or "Abomination Map" or "Ziggurat Map");
        Assert.Equal(2, maps.Count(Influenced));
        Assert.Single(maps, item => catalog.ByHash(item.Item.TypeHash)!.Name == "Map (Tier 16)" && Influenced(item));
        Assert.DoesNotContain(maps, item => item.Item.Rarity == ItemRarity.Unique);

        Assert.Equal(28, uniques.Count);
        Assert.All(uniques, item =>
        {
            Assert.Equal(ItemRarity.Unique, item.Item.Rarity);
            Assert.NotEmpty(item.Item.NameWords);
            Assert.NotEmpty(item.Item.Payload());
        });
        Assert.DoesNotContain(tabs.Where(t => t.Name.StartsWith("Goods")).SelectMany(t => t.Items),
            item => catalog.ByHash(item.Item.TypeHash) is { Class: "Map" or "MapKey" });
        Assert.All(maps, item => Assert.NotEmpty(item.Item.Payload()));
        Assert.All(maps, item => Assert.NotEmpty(item.Item.Price));
    }
}

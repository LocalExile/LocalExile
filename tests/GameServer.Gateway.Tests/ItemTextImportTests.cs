using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemTextImportTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;

    const string RingText = """
Rarity: Rare
Sorrow Twirl
Amethyst Ring
--------
Quality (Attribute Modifiers): +20%
--------
Requirements:
Level: 65
--------
Item Level: 83
--------
+22% to Chaos Resistance (implicit)
--------
+62 to Strength (fractured)
+4 to maximum Energy Shield
+101 to maximum Life
+35% to Fire Resistance
+20% to Chaos Resistance
Non-Channelling Skills have -7 to Total Mana Cost (crafted)
--------
Fractured Item
""";

    [Fact]
    public void Reads_the_base_properties_and_common_ring_mods()
    {
        var result = new ItemTextImport(Catalog).Parse(RingText, out string error);

        Assert.Equal("", error);
        Assert.NotNull(result);
        Assert.Equal("Amethyst Ring", result!.Base.Name);
        Assert.Equal(ItemRarity.Rare, result.Rarity);
        Assert.Equal(83, result.ItemLevel);
        Assert.Equal(20, result.Quality);
        Assert.Contains("Attribute", result.QualityType);
        Assert.True(result.Mods.Count == 6, string.Join(" || ", result.Unmatched));
        Assert.Empty(result.Unmatched);
        var strength = Assert.Single(result.Mods, m => m.Fractured);
        Assert.Equal(52, Assert.Single(strength.Values));
        var crafted = Assert.Single(result.Mods, m => m.Crafted);
        Assert.Equal(-7, Assert.Single(crafted.Values));
        Assert.Contains(result.Mods, m => m.Stats.Any(s => s.Id == "base_maximum_life") && m.Values[0] == 101);
        Assert.Contains(result.Mods, m => m.Stats.Any(s => s.Id == "base_fire_damage_resistance_%") && m.Values[0] == 35);
        Assert.Contains(result.Mods, m => m.Stats.Any(s => s.Id == "base_chaos_damage_resistance_%") && m.Values[0] == 20);
    }

    [Fact]
    public void Keeps_a_fractured_modifier_first_when_building_the_copy()
    {
        var parsed = new ItemTextImport(Catalog).Parse(RingText, out _)!;
        var choices = parsed.Mods.Select(m => new ItemTool.ModChoice(m.Hash, m.Values, m.Fractured, m.Crafted)).ToArray();
        var built = ItemTool.Build(Catalog, BenchCraftingTests.Setup.Value.Rolls, parsed.Base, parsed.Rarity,
            new ItemTool.CreateRequest(1, parsed.Base.Hash, parsed.Rarity.ToString(), parsed.ItemLevel, parsed.Quality, 1, choices),
            new Random(42));

        Assert.Null(built.Error);
        Assert.Equal(1, built.Item!.FracturedCount);
        var firstExplicit = built.Item.Mods.First(m => m.Section == ModSection.Explicit);
        Assert.Equal(choices.First(c => c.Fractured).Hash, firstExplicit.Hash);
    }
}

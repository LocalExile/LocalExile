using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class InfluencedJewelTests
{
    [Fact]
    public void Multiple_influence_requirements_use_item_counts_and_contribute_to_actual_attribute_inputs()
    {
        var items = Program.Items;
        var jewel = ItemCatalog.Create(items.Find("Prismatic Jewel")!) with
        {
            Rarity = ItemRarity.Unique, Mods =
            [
                new(ModSection.Explicit, items.Mods.ById("MaximumLifeIncreasePercent2ElderItemsUnique__1")!.Hash, [15]),
                new(ModSection.Explicit, items.Mods.ById("PercentageStrength2WarlordItemsUnique__1")!.Hash, [12]),
            ],
        };
        var baseItem = ItemCatalog.Create(items.Find("Iron Hat")!);
        var both = baseItem with { Influences = ItemInfluence.Elder | ItemInfluence.Warlord };
        var elder = baseItem with { Influences = ItemInfluence.Elder };
        var warlord = baseItem with { Influences = ItemInfluence.Warlord };
        uint life = Program.Stats.Key("maximum_life_+%")!.Value;
        var oneEach = ItemModStats.Global([both, jewel], items.Mods, Program.Stats);
        Assert.Equal(0, oneEach.GetValueOrDefault(life));
        var twoElder = ItemModStats.Global([both, elder, jewel], items.Mods, Program.Stats);
        Assert.Equal(15, twoElder[life]);
        Assert.Equal(0, ItemModStats.RawAttributes([both, elder, jewel], items.Mods).GetValueOrDefault("strength_+%"));
        Assert.Equal(12, ItemModStats.RawAttributes([both, warlord, jewel], items.Mods)["strength_+%"]);
    }

    [Fact]
    public void Six_influenced_items_enable_extra_projectiles_and_each_element_separately()
    {
        var items = Program.Items;
        var jewel = ItemCatalog.Create(items.Find("Prismatic Jewel")!) with
        {
            Rarity = ItemRarity.Unique, Mods =
            [
                new(ModSection.Explicit, items.Mods.ById("AdditionalProjectile6HunterItemsUnique__1")!.Hash, [1]),
                new(ModSection.Explicit, items.Mods.ById("PhysAddedAsEachElement6ShaperItemsUnique__1")!.Hash, [12]),
            ],
        };
        var gear = ItemCatalog.Create(items.Find("Iron Hat")!) with { Influences = ItemInfluence.Shaper | ItemInfluence.Hunter };
        var five = ItemModStats.Global(Enumerable.Repeat(gear, 5).Append(jewel), items.Mods, Program.Stats);
        var six = ItemModStats.Global(Enumerable.Repeat(gear, 6).Append(jewel), items.Mods, Program.Stats);
        foreach (string id in new[] { "number_of_additional_projectiles", "physical_damage_%_to_add_as_fire",
            "physical_damage_%_to_add_as_cold", "physical_damage_%_to_add_as_lightning" })
        {
            uint key = Program.Stats.Key(id)!.Value;
            Assert.Equal(0, five.GetValueOrDefault(key));
            Assert.Equal(id == "number_of_additional_projectiles" ? 1 : 12, six[key]);
        }
    }
}

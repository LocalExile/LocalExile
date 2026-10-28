using GameServer.Core.Accounts;
using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class PathOfBuildingSpecialDefenceTests
{
    // PoB omits zero defence lines when exporting an item. Its native hidden modifier must still be restored.
    const string Tabula = "Rarity: UNIQUE\nTabula Rasa\nSimple Robe\nItem Level: 1\nQuality: 20\n"
        + "Sockets: W-W-W-W-W-W\nImplicits: 0";

    const string Scales = "Rarity: UNIQUE\nThe Scales of Justice\nChiming Spirit Shield\nItem Level: 85\n"
        + "Quality: 20\nImplicits: 1\n12% increased Spell Damage\nHas no Energy Shield";

    static ItemCatalog Catalog => Program.Items;

    static Item Import(string text, string slot = "Body Armour")
    {
        var build = new PathOfBuildingBuild(CharacterClass.Witch, 20,
            new(3, 0, "3_29", [], new Dictionary<ushort, ushort>(), [], false),
            [new(1, text, null)], [new(slot, 1)], [], null);
        var result = PathOfBuildingItems.Materialize(build, Catalog);
        var saved = Assert.Single(result.Inventories.Values.SelectMany(items => items)).Item;
        return CharacterSaves.FromSaved(saved, Catalog);
    }

    [Fact]
    public void A_real_Tabula_export_without_an_ES_line_keeps_its_hidden_native_zero_ES_rule()
    {
        var item = Import(Tabula);

        Assert.Equal(ItemRarity.Unique, item.Rarity);
        Assert.Equal(20, item.Quality);
        Assert.Equal(6, item.Sockets.Count);
        Assert.All(item.Sockets, socket => Assert.Equal(SocketColour.White, socket.Colour));
        var local = ItemModStats.Local(item, Catalog.Mods);
        Assert.Equal(1, local.GetValueOrDefault("local_unique_tabula_rasa_no_requirement_or_energy_shield"));
        var type = Catalog.ByHash(item.TypeHash)!;
        Assert.Equal(0, ItemDefences.Calculate(item, Catalog.Defences(type)!.Value, Catalog.Mods).EnergyShield);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.5", 16)]
    [InlineData("1", 31)]
    public void An_explicit_base_percentile_is_preserved_when_hidden_ES_cannot_disambiguate_the_roll(
        string percentile, byte expectedRoll)
    {
        var item = Import(Tabula + $"\nEnergyShieldBasePercentile: {percentile}\nEnergy Shield: 0");

        Assert.Equal(expectedRoll, item.DefenceBaseRoll);
        Assert.Equal(0, ItemDefences.Calculate(item, Catalog.Defences(Catalog.ByHash(item.TypeHash)!)!.Value,
            Catalog.Mods).EnergyShield);
    }

    [Fact]
    public void A_positive_ES_display_cannot_override_Tabulas_hidden_zero_ES_rule()
    {
        var error = Assert.Throws<ArgumentException>(() => Import(Tabula + "\nEnergy Shield: 1"));

        Assert.Contains("defence values cannot be represented", error.Message);
    }

    [Fact]
    public void A_known_hidden_unique_cannot_be_imported_on_an_unrelated_armour_base()
    {
        var error = Assert.Throws<ArgumentException>(() => Import(Tabula.Replace("Simple Robe", "Plate Vest")));

        Assert.Contains("Tabula Rasa", error.Message);
        Assert.Contains("base", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_explicit_no_ES_modifier_matches_a_zero_display_without_changing_its_exported_percentile()
    {
        var item = Import(Scales + "\nEnergyShieldBasePercentile: 0.5\nEnergy Shield: 0", "Weapon 2");

        Assert.Equal(16, item.DefenceBaseRoll);
        Assert.Equal(1, ItemModStats.Local(item, Catalog.Mods).GetValueOrDefault("local_no_energy_shield"));
        Assert.Equal(0, ItemDefences.Calculate(item, Catalog.Defences(Catalog.ByHash(item.TypeHash)!)!.Value,
            Catalog.Mods).EnergyShield);
    }

    [Fact]
    public void A_positive_ES_display_is_refused_when_the_native_modifier_forbids_item_ES()
    {
        var error = Assert.Throws<ArgumentException>(() => Import(Scales + "\nEnergy Shield: 1", "Weapon 2"));

        Assert.Contains("defence values cannot be represented", error.Message);
    }

    [Theory]
    [InlineData("Skin of the Loyal", "Simple Robe", "RemoveStatsUnique__1")]
    [InlineData("Skin of the Lords", "Simple Robe", "RemoveStatsUnique__1")]
    [InlineData("The Apostate", "Cabalist Regalia", "RemoveStatsUnique__2")]
    public void Supported_unique_identities_restore_the_hidden_modifier_on_their_exact_native_base(
        string name, string baseName, string hiddenMod)
    {
        // These minimal item definitions exercise identity restoration, not every visible modifier of each unique.
        var item = Import($"Rarity: UNIQUE\n{name}\n{baseName}\nItem Level: 85\nQuality: 20\n"
            + "EnergyShieldBasePercentile: 1\nEnergy Shield: 0\nImplicits: 0");

        var hidden = Assert.Single(item.Mods, mod => mod.Section == ModSection.Hidden);
        Assert.Equal(hiddenMod, Catalog.Mods.ByHash(hidden.Hash)!.Id);
        Assert.Equal(31, item.DefenceBaseRoll);
        Assert.Equal(0, ItemDefences.Calculate(item, Catalog.Defences(Catalog.ByHash(item.TypeHash)!)!.Value,
            Catalog.Mods).EnergyShield);
    }

    [Theory]
    [InlineData("Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items",
        "LifeFromEnergyShieldArmourUnique__1", "gain_maximum_life_instead_of_maximum_es_from_armour", 1)]
    [InlineData("Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items at 50% of the value",
        "DivergentLifeFromEnergyShieldArmourPercentUnique__1",
        "gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value", 50)]
    public void Supported_conversion_text_resolves_to_the_exact_native_modifier_and_value(
        string text, string modId, string stat, int value)
    {
        var item = Import("Rarity: UNIQUE\nThe Apostate\nCabalist Regalia\nItem Level: 85\nImplicits: 0\n" + text);

        var conversion = Assert.Single(item.Mods, mod => mod.Section == ModSection.Explicit);
        Assert.Equal(modId, Catalog.Mods.ByHash(conversion.Hash)!.Id);
        Assert.Equal(value, Assert.Single(conversion.Values));
        Assert.Equal(value, ItemModStats.Sum([item], Catalog.Mods).GetValueOrDefault(stat));
        Assert.Single(item.Mods, mod => mod.Section == ModSection.Hidden);
    }

    [Fact]
    public void A_conversion_percentage_absent_from_native_mod_data_is_omitted_without_substitution()
    {
        var item = Import(
            "Rarity: UNIQUE\nThe Apostate\nCabalist Regalia\nItem Level: 85\nImplicits: 0\n"
            + "Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items at 60% of the value");

        Assert.DoesNotContain(item.Mods, m => m.Section == ModSection.Explicit);
        Assert.Single(item.Mods, m => m.Section == ModSection.Hidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conditional_synthesis_increase_does_not_change_the_exported_tooltip_or_selected_base_roll(
        bool corrupted)
    {
        var text = "Rarity: NORMAL\nSimple Robe\nItem Level: 85\nQuality: 20\n"
            + "EnergyShieldBasePercentile: 0.5\nEnergy Shield: 49\nImplicits: 1\n"
            + "10% increased maximum Energy Shield if Corrupted\n+10 to maximum Energy Shield\n"
            + "50% increased Energy Shield" + (corrupted ? "\nCorrupted" : "");
        var item = Import(text);

        Assert.Equal(corrupted, item.Corrupted);
        Assert.Equal(16, item.DefenceBaseRoll);
        var implicitMod = Assert.Single(item.Mods, mod => mod.Section == ModSection.Implicit);
        Assert.Equal("SynthesisImplicitIncreasedEnergyShieldCorruptedItem1", Catalog.Mods.ByHash(implicitMod.Hash)!.Id);
        Assert.Equal(10, Assert.Single(implicitMod.Values));
        Assert.Equal(49, ItemDefences.Calculate(item, Catalog.Defences(Catalog.ByHash(item.TypeHash)!)!.Value,
            Catalog.Mods).EnergyShield);
    }
}

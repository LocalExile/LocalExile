using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class PercentageAttributeEquipmentTests
{
    static readonly Lazy<ItemCatalog> Ordinary = new(() => new(BenchCraftingTests.Data()));
    static ModCatalog Mods() => Ordinary.Value.Mods;

    // Controlled source combinations test qualification, not legal acquisition of a named unique. Every percentage
    // below uses its actual native modifier row and value; flat helpers isolate the indicated native stat.
    static Item Source(ModCatalog mods, params (string Id, int Value)[] values) => new(1, ["Mods"], 0)
    {
        Mods = values.Select(v =>
        {
            var mod = mods.ById(v.Id)!;
            Assert.Single(mod.Stats);
            Assert.False(mod.Stats[0].Local);
            Assert.InRange(v.Value, mod.Stats[0].Min, mod.Stats[0].Max);
            return new ItemMod(ModSection.Explicit, mod.Hash, [v.Value]);
        }).ToArray(),
    };

    static Item Flat(ModCatalog mods, string stat, int value)
    {
        var mod = mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == stat && !m.Stats[0].Local);
        return new(1, ["Mods"], 0) { Mods = [new(ModSection.Explicit, mod.Hash, [value])] };
    }

    static ItemModStats.Worn Wear(uint id, Item? item = null, int str = 0, int dex = 0, int intel = 0, int level = 1) =>
        new(id, item ?? new(1, ["Mods"], 0), str, dex, intel, level);

    static (int Str, int Dex, int Int) Total(ModCatalog mods, IReadOnlyList<Item> items,
        (int Str, int Dex, int Int) basis)
    {
        var stats = ItemModStats.Sum(items, mods);
        int Get(string key) => stats.GetValueOrDefault(key);
        return CharacterStats.ResolveAttributes(basis, CharacterStats.Attributes(Get),
            (Get("strength_+%"), Get("dexterity_+%"), Get("intelligence_+%")), Get("all_attributes_+%"));
    }

    [Fact]
    public void Whole_base_and_all_active_flats_share_typed_and_all_increases()
    {
        var mods = Mods();
        ItemModStats.Worn[] worn =
        [
            Wear(1, Flat(mods, "additional_strength", 50)),
            Wear(2, Source(mods, ("PercentageStrengthUniqueBootsStrInt2", 18))),
            Wear(3, Source(mods, ("TalismanIncreasedAllAttributes", 16))),
            Wear(4, str: 200),
        ];
        var result = ItemModStats.ResolveEquipment(worn, (100, 0, 0), 100, mods);

        Assert.Empty(result.Inactive);
        // (100 class/passive + 50 item) * (1 + (18 + 16)/100) = 201. Item-by-item scaling or multiplying the
        // increases would produce a different answer and can change the last item's qualification.
        Assert.Equal((201, 0, 0), Total(mods, result.Active, (100, 0, 0)));
    }

    [Fact]
    public void Inactive_percentage_source_cannot_reactivate_using_its_own_increase()
    {
        var mods = Mods();
        var percentage = Wear(1, Source(mods, ("PercentageStrengthUniqueBootsStrInt2", 18)), str: 110);

        var inactive = ItemModStats.ResolveEquipment([percentage], (100, 0, 0), 100, mods, new HashSet<uint> { 1 });
        Assert.Equal(new uint[] { 1 }, inactive.Inactive.Order());
        Assert.Empty(inactive.Active);

        // An already active item keeps counting its own input, as with the existing flat-attribute policy.
        var active = ItemModStats.ResolveEquipment([percentage], (100, 0, 0), 100, mods);
        Assert.Empty(active.Inactive);
        Assert.Equal((118, 0, 0), Total(mods, active.Active, (100, 0, 0)));

        var support = Wear(2, Flat(mods, "additional_strength", 10));
        var restored = ItemModStats.ResolveEquipment([percentage, support], (100, 0, 0), 100, mods, inactive.Inactive);
        Assert.Empty(restored.Inactive);
        Assert.Equal((130, 0, 0), Total(mods, restored.Active, (100, 0, 0)));
    }

    [Fact]
    public void Removal_cascades_after_percentage_source_fails_and_external_support_recovers_it()
    {
        var mods = Mods();
        var percentage = Wear(1, Source(mods, ("PercentageStrengthUniqueBootsStrInt2", 18)), str: 120);
        var dependent = Wear(2, str: 140);
        var support = Wear(3, Flat(mods, "additional_strength", 20));
        var before = ItemModStats.ResolveEquipment([percentage, dependent, support], (100, 0, 0), 100, mods);
        Assert.Empty(before.Inactive);

        var removed = ItemModStats.ResolveEquipment([percentage, dependent], (100, 0, 0), 100, mods, before.Inactive);
        Assert.Equal(new uint[] { 1, 2 }, removed.Inactive.Order());
        Assert.Empty(removed.Active);

        var restored = ItemModStats.ResolveEquipment([percentage, dependent, support], (100, 0, 0), 100, mods, removed.Inactive);
        Assert.Empty(restored.Inactive);
        Assert.Equal((142, 0, 0), Total(mods, restored.Active, (100, 0, 0)));
    }

    [Fact]
    public void Real_negative_percentage_disables_other_gear_and_removal_restores_it()
    {
        var mods = Mods();
        var reduction = Wear(1, Source(mods, ("PercentageStrengthUnique__4_", -10)));
        var dependent = Wear(2, str: 95);
        var result = ItemModStats.ResolveEquipment([reduction, dependent], (100, 0, 0), 100, mods);
        Assert.Equal(new uint[] { 2 }, result.Inactive.Order());
        Assert.Equal((90, 0, 0), Total(mods, result.Active, (100, 0, 0)));

        var again = ItemModStats.ResolveEquipment([reduction, dependent], (100, 0, 0), 100, mods, result.Inactive);
        Assert.True(result.Inactive.SetEquals(again.Inactive));
        var restored = ItemModStats.ResolveEquipment([dependent], (100, 0, 0), 100, mods, result.Inactive);
        Assert.Empty(restored.Inactive);
    }

    [Fact]
    public void Real_minus_one_hundred_typed_sources_leave_zero_attributes_and_no_dependent_gear()
    {
        var mods = Mods();
        var zero = Source(mods, ("PercentageStrengthUnique__1", -100),
            ("PercentageDexterityUnique__1", -100), ("PercentageIntelligenceUnique__1", -100));
        var result = ItemModStats.ResolveEquipment(
            [Wear(1, zero), Wear(2, str: 1), Wear(3, dex: 1), Wear(4, intel: 1)], (100, 100, 100), 100, mods);

        Assert.Equal(new uint[] { 2, 3, 4 }, result.Inactive.Order());
        Assert.Single(result.Active);
        Assert.Equal((0, 0, 0), Total(mods, result.Active, (100, 100, 100)));
    }

    [Fact]
    public void Whole_active_set_callback_recalculates_after_each_deactivation()
    {
        var mods = Mods();
        var reduction = Wear(1, Source(mods, ("PercentageStrengthUnique__4_", -10)), intel: 110);
        var dependent = Wear(2, str: 95);
        var observations = new List<(int Count, int Str)>();
        var result = ItemModStats.ResolveEquipment([reduction, dependent], 100, mods, active =>
        {
            var total = Total(mods, active, (100, 100, 100));
            observations.Add((active.Count, total.Str));
            return total;
        });

        // First item fails Intelligence. Recomputing without its negative Strength input allows the next item to stay.
        Assert.Equal(new uint[] { 1 }, result.Inactive.Order());
        Assert.Single(result.Active);
        Assert.Contains((2, 90), observations);
        Assert.Contains((1, 100), observations);
    }

    [Theory]
    [InlineData("local_no_attribute_requirements")]
    [InlineData("local_unique_tabula_rasa_no_requirement_or_energy_shield")]
    public void Both_equipment_overloads_share_no_attribute_requirement_and_level_rules(string flag)
    {
        var mods = Mods();
        var noRequirements = mods.All.First(m => m.Stats.Any(s => s.Id == flag));
        var item = new Item(1, ["Mods"], 0)
        {
            Mods = [new(ModSection.Hidden, noRequirements.Hash,
                noRequirements.Stats.Select(s => s.Id == flag ? 1 : 0).ToArray())],
        };
        var worn = Wear(1, item, str: 500, dex: 500, intel: 500, level: 10);

        var tupleLow = ItemModStats.ResolveEquipment([worn], (0, 0, 0), 9, mods);
        var callbackLow = ItemModStats.ResolveEquipment([worn], 9, mods, _ => (0, 0, 0));
        Assert.Equal(new uint[] { 1 }, tupleLow.Inactive.Order());
        Assert.True(tupleLow.Inactive.SetEquals(callbackLow.Inactive));
        Assert.Empty(ItemModStats.ResolveEquipment([worn], (0, 0, 0), 10, mods, tupleLow.Inactive).Inactive);
        Assert.Empty(ItemModStats.ResolveEquipment([worn], 10, mods, _ => (0, 0, 0), callbackLow.Inactive).Inactive);
    }

    [Fact]
    public void Signed_mutual_dependency_remains_an_unresolved_repeated_evaluation_boundary()
    {
        var mods = Mods();
        ItemModStats.Worn[] worn =
        [
            Wear(1, Flat(mods, "additional_intelligence", 10), intel: 110),
            Wear(2, Source(mods, ("PercentageStrengthUnique__4_", -10),
                ("PercentageIntelligenceUnique__1", -100)), str: 95, intel: 110),
            Wear(3, Source(mods, ("PercentageStrengthUnique__4_", -10),
                ("PercentageIntelligenceUniqueHelmetStrDex6", 10)), str: 100, intel: 100),
        ];
        // This deliberately documents the existing local two-phase boundary, not an official ordering or legal
        // named-unique combination. No native/reference evidence justifies inventing a stabilization policy here.
        var first = ItemModStats.ResolveEquipment(worn, (100, 0, 100), 100, mods, new HashSet<uint> { 1, 2, 3 });
        Assert.Equal(new uint[] { 2, 3 }, first.Inactive.Order());
        var second = ItemModStats.ResolveEquipment(worn, (100, 0, 100), 100, mods, first.Inactive);
        Assert.Equal(new uint[] { 1, 2, 3 }, second.Inactive.Order());
        var third = ItemModStats.ResolveEquipment(worn, (100, 0, 100), 100, mods, second.Inactive);
        Assert.True(first.Inactive.SetEquals(third.Inactive));
    }
}

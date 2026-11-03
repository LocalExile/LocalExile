using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The item-local increased-effect stats of the Simplex bases: explicit modifiers of the matching side have their
// values raised, implicits do not, and the same implicits move the rare affix caps.
public sealed class ItemModEffectTests
{
    static Item With(ItemCatalog items, Item item, string id, ModSection section, params int[] values) =>
        item with { Mods = [.. item.Mods, items.Mods.ById(id)!.With(section, values, new Random(1))] };

    [Fact]
    public void Simplex_amulet_doubles_its_explicit_modifiers_and_leaves_implicits_alone()
    {
        var items = new ItemCatalog("data");
        var item = ItemCatalog.Create(items.All.First(t => t.Name == "Simplex Amulet"));
        Assert.Contains(item.Mods, m => items.Mods.ByHash(m.Hash)?.Id == "MaxPrefixMaxSuffixModEffectImplicitE2");
        item = With(items, item, "IncreasedLife7", ModSection.Explicit, 80);
        item = With(items, item, "StrengthImplicitBelt1", ModSection.Implicit, 20);

        var stats = ItemModStats.Sum([item], items.Mods);
        Assert.Equal(160, stats.GetValueOrDefault("base_maximum_life"));
        Assert.Equal(20, stats.GetValueOrDefault("additional_strength"));
        // The effect stat itself is never scaled, or it would raise itself.
        Assert.Equal(100, ItemModStats.Local(item, items.Mods).GetValueOrDefault("local_explicit_mod_effect_+%"));
    }

    [Fact]
    public void Ring_bases_raise_only_their_own_side_and_move_the_rare_affix_caps()
    {
        var items = new ItemCatalog("data");
        var rolls = new ItemRolls(items);
        var cogwork = ItemCatalog.Create(items.All.First(t => t.Name == "Cogwork Ring"));
        cogwork = With(items, cogwork, "IncreasedLife7", ModSection.Explicit, 80);
        cogwork = With(items, cogwork, "FireResist1", ModSection.Explicit, 40);

        var stats = ItemModStats.Sum([cogwork], items.Mods);
        Assert.Equal(80, stats.GetValueOrDefault("base_maximum_life"));
        Assert.Equal(50, stats.GetValueOrDefault("base_fire_damage_resistance_%")); // 40 * 1.25
        Assert.Equal((2, 4), rolls.RareMaxAffixes(cogwork));

        var composite = ItemCatalog.Create(items.All.First(t => t.Name == "Composite Ring"));
        Assert.Equal((6, 0), rolls.RareMaxAffixes(composite));
    }
}

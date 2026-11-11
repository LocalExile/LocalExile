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
        var local = ItemModStats.Local(item, items.Mods);
        local.Clear();
        Assert.Equal(100, ItemModStats.Local(item, items.Mods).GetValueOrDefault("local_explicit_mod_effect_+%"));
        var changed = item with
        {
            Mods = [.. item.Mods.Select(m => m with { Values = [.. m.Values.Select(v => v + 1)] })],
        };
        Assert.Equal(ItemModStats.Sum([changed], items.Mods, local: true).OrderBy(p => p.Key),
            ItemModStats.Local(changed, items.Mods).OrderBy(p => p.Key));
        Assert.NotEqual(ItemModStats.Local(item, items.Mods).GetValueOrDefault("local_explicit_mod_effect_+%"),
            ItemModStats.Local(changed, items.Mods).GetValueOrDefault("local_explicit_mod_effect_+%"));
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

    [Fact]
    public void Heist_defence_magnitude_applies_to_eligible_explicit_mods_on_its_own_item()
    {
        using var gameRules = new GameRules();
        var items = new ItemCatalog("data");
        var plain = ItemCatalog.Create(items.All.First(t => t.Name == "Twilight Regalia")) with { Mods = [] };
        plain = With(items, plain, "LocalIncreasedEnergyShield1", ModSection.Explicit, 100);
        plain = With(items, plain, "LocalIncreasedEnergyShieldPercent1", ModSection.Explicit, 110);
        plain = With(items, plain, "BreachBodyAllDefences1", ModSection.Explicit, 50);
        plain = With(items, plain, "ReducedEnergyShieldPercentUniqueAmulet13", ModSection.Explicit, -50);
        plain = With(items, plain, "FireResist1", ModSection.Explicit, 25);
        plain = With(items, plain, "LocalIncreasedEnergyShield2", ModSection.Implicit, 10);
        var item = With(items, plain, "ArmourEnchantmentHeistDefenceEffectAttributeRequirements1", ModSection.Enchant, 15, -25);

        Program.SetGameLogic(null!);
        Assert.Equal(110, ItemModStats.Local(item, items.Mods)["local_energy_shield"]);
        Assert.Equal(50, ItemModStats.Sum([item], items.Mods)["global_defences_+%"]);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var local = ItemModStats.Local(item, items.Mods);
        Assert.Equal(125, local["local_energy_shield"]); // 115 from the explicit and 10 from the implicit.
        Assert.Equal(126, local["local_energy_shield_+%"]);
        Assert.Equal(15, local["heist_enchantment_defence_mod_effect_+%"]);
        Assert.Equal(-25, local["local_attribute_requirements_+%"]);

        var global = ItemModStats.Sum([item], items.Mods);
        Assert.Equal(57, global["global_defences_+%"]);
        Assert.Equal(-57, global["maximum_energy_shield_+%"]);
        Assert.Equal(25, global["base_fire_damage_resistance_%"]);
        Assert.Equal(107, ItemModStats.Sum([item, plain], items.Mods)["global_defences_+%"]);
        Assert.Equal(new[] { 100, 110, 50, -50, 25, 10, 15, -25 }, item.Mods.SelectMany(mod => mod.Values));

        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        Assert.Equal(125, ItemModStats.Local(item, items.Mods)["local_energy_shield"]);
        Program.SetGameLogic(null!);
        Assert.Equal(110, ItemModStats.Local(item, items.Mods)["local_energy_shield"]);
        Assert.Equal(-50, ItemModStats.Sum([item], items.Mods)["maximum_energy_shield_+%"]);
    }

    [Fact]
    public void Heist_weapon_magnitudes_match_element_tags_and_combine_before_rounding()
    {
        using var gameRules = new GameRules();
        var items = new ItemCatalog("data");
        var weapon = ItemCatalog.Create(items.All.First(t => t.Name == "Driftwood Wand")) with { Mods = [] };
        (string Element, string Affix, string Enchantment)[] variants =
        [
            ("fire", "LocalAddedFireDamage1", "WeaponEnchantmentHeistFireEffect1"),
            ("cold", "LocalAddedColdDamage1", "WeaponEnchantmentHeistColdEffect1__"),
            ("lightning", "LocalAddedLightningDamage1", "WeaponEnchantmentHeistLightningEffect1"),
        ];
        foreach (var (_, affix, _) in variants)
            weapon = With(items, weapon, affix, ModSection.Explicit, 33, 47);

        foreach (var (matched, _, enchantment) in variants)
        {
            var enchanted = With(items, weapon, enchantment, ModSection.Enchant, 8);
            var local = ItemModStats.Local(enchanted, items.Mods);
            foreach (var (element, _, _) in variants)
            {
                Assert.Equal(element == matched ? 35 : 33, local[$"local_minimum_added_{element}_damage"]);
                Assert.Equal(element == matched ? 50 : 47, local[$"local_maximum_added_{element}_damage"]);
            }
        }

        var caster = With(items, weapon, "SpellDamage1", ModSection.Explicit, 33);
        caster = With(items, caster, "IncreasedCastSpeed1", ModSection.Explicit, 33);
        caster = With(items, caster, "WeaponEnchantmentHeistCasterDamageEffect1", ModSection.Enchant, 8);
        var casterStats = ItemModStats.Sum([caster], items.Mods);
        Assert.Equal(35, casterStats["spell_damage_+%"]);
        Assert.Equal(33, casterStats["base_cast_speed_+%"]);

        var dual = With(items, weapon, "ChanceToAvoidIgniteEssence4", ModSection.Explicit, 43);
        dual = With(items, dual, "WeaponEnchantmentHeistFireEffectAilmentEffect1_", ModSection.Enchant, 6, 6);
        Assert.Equal(48, ItemModStats.Sum([dual], items.Mods)["base_avoid_ignite_%"]);

        var penalty = With(items, weapon, "LocalIncreasedAttackSpeed1", ModSection.Explicit, 20);
        penalty = With(items, penalty, "LocalReducedPhysicalDamagePercentUniqueBow8", ModSection.Explicit, -120);
        penalty = With(items, penalty, "WeaponEnchantmentHeistSpeedEffectDamageEffectPenalty1", ModSection.Enchant, 12, -25);
        var penalized = ItemModStats.Local(penalty, items.Mods);
        Assert.Equal(22, penalized["local_attack_speed_+%"]);
        Assert.Equal(24, penalized["local_minimum_added_fire_damage"]);
        Assert.Equal(-120, penalized["local_weapon_no_physical_damage"]);
    }

    sealed class GameRules : IDisposable
    {
        readonly IGameLogic? previous = Program.GameLogicOrNull;

        public GameRules() => Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        public void Dispose() => Program.SetGameLogic(previous!);
    }
}

// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChargeEffectsTests
{
    static readonly Lazy<StatCatalog> Catalog = new(() => new StatCatalog("data"));

    // The character's stats: Character.ot's per-charge stats plus the given ones.
    static Func<string, int> Character(params (string Name, int Value)[] stats)
    {
        var all = ChargeEffects.Inherent.Select(i => (Name: i.Stat, i.Value)).Concat(stats).ToList();
        return name => all.Where(s => s.Name == name).Sum(s => s.Value);
    }

    static Func<ChargeType, int> Held(int endurance = 0, int frenzy = 0, int power = 0) =>
        t => t switch { ChargeType.Endurance => endurance, ChargeType.Frenzy => frenzy, _ => power };

    // The expanded stats added onto the character's, as WithCharges does.
    static Func<string, int> With(Func<string, int> character, Func<ChargeType, int> held)
    {
        var added = ChargeEffects.Expand(character, held);
        var display = ChargeEffects.Display(held, t => new Charges.Limits(3, 0, 10_000)).ToDictionary(d => d.Stat, d => d.Value);
        return name => character(name) + added.GetValueOrDefault(name) + display.GetValueOrDefault(name);
    }

    [Fact]
    public void The_inherent_bonuses_are_Character_ot_s()
    {
        // Metadata/Characters/Character.ot (3.29.3b).
        Assert.Equal(
        [
            ("base_attack_speed_+%_per_frenzy_charge", 4), ("base_cast_speed_+%_per_frenzy_charge", 4),
            ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("physical_damage_reduction_%_per_endurance_charge", 4),
            ("elemental_damage_reduction_%_per_endurance_charge", 4), ("critical_strike_chance_+%_per_power_charge", 50),
        ], ChargeEffects.Inherent);
    }

    [Fact]
    public void Every_stat_of_the_tables_exists_in_Stats_dat()
    {
        var names = ChargeEffects.Inherent.Select(i => i.Stat)
            .Concat(ChargeEffects.PerCharge.SelectMany(r => r.Targets.Prepend(r.Stat)))
            .Concat(ChargeEffects.Conditional.SelectMany(c => c.Targets.Prepend(c.Stat)))
            .Concat(Charges.Types.SelectMany(t => new[] { $"current_{Charges.Name(t)}_charges", $"actual_current_{Charges.Name(t)}_charges",
                $"minimum_{Charges.Name(t)}_charges", $"current_removable_{Charges.Name(t)}_charges", $"base_minimum_{Charges.Name(t)}_charges" }))
            .Append("additional_physical_damage_reduction_%").Append("additional_elemental_damage_reduction_%")
            .Append("base_additional_physical_damage_reduction_%").Append("object_inherent_spell_damage_+%_final_per_power_charge")
            .Append("quiet_might_keystone_spell_damage_+%_final_per_power_charge");
        Assert.DoesNotContain(names, n => Catalog.Value.Key(n) is null);
        Assert.Equal(ChargeEffects.PerCharge.Count, ChargeEffects.PerCharge.Select(r => r.Stat).Distinct().Count());
    }

    [Fact]
    public void Without_charges_nothing_is_added()
    {
        Assert.Empty(ChargeEffects.Expand(Character(), Held()));
    }

    [Fact]
    public void Extreme_counts_saturate_the_all_charge_sum_without_overflow()
    {
        var get = Character(("damage_+%_per_frenzy_power_or_endurance_charge", int.MaxValue));
        var added = ChargeEffects.Expand(get, Held(int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.Equal(int.MaxValue, added["damage_+%"]);
    }

    [Fact]
    public void The_standard_bonuses_scale_with_the_charges_held()
    {
        var added = ChargeEffects.Expand(Character(), Held(endurance: 2, frenzy: 3, power: 3));
        Assert.Equal(12, added["attack_speed_+%"]);
        Assert.Equal(12, added["base_cast_speed_+%"]);
        Assert.Equal(8, added["additional_physical_damage_reduction_%"]);
        Assert.Equal(8, added["additional_elemental_damage_reduction_%"]);
        Assert.Equal(150, added["critical_strike_chance_+%"]);
        // The more damage per Frenzy Charge is no plain stat: DamageMore reads it.
        Assert.False(added.ContainsKey("damage_+%"));
    }

    [Fact]
    public void Per_charge_mods_become_the_stats_the_server_calculates_with()
    {
        var get = Character(("movement_velocity_+%_per_frenzy_charge", 2), ("resist_all_elements_%_per_endurance_charge", 5),
            ("spell_damage_+%_per_power_charge", 6), ("minimum_added_fire_damage_per_endurance_charge", 3),
            ("maximum_added_fire_damage_per_endurance_charge", 7), ("damage_+%_per_frenzy_power_or_endurance_charge", 2),
            ("damage_+%_per_power_charge", 1), ("skill_area_of_effect_+%_per_power_charge_up_to_50%", 20),
            ("accuracy_rating_+%_per_frenzy_charge", 4), ("base_additional_physical_damage_reduction_%", 5));
        var added = ChargeEffects.Expand(get, Held(endurance: 2, frenzy: 3, power: 3));
        Assert.Equal(6, added["base_movement_velocity_+%"]);
        Assert.Equal(10, added["base_resist_all_elements_%"]);
        Assert.Equal(18, added["spell_damage_+%"]);
        Assert.Equal(6, added["attack_minimum_added_fire_damage"]);
        Assert.Equal(6, added["spell_minimum_added_fire_damage"]);
        Assert.Equal(14, added["attack_maximum_added_fire_damage"]);
        Assert.Equal(14, added["spell_maximum_added_fire_damage"]);
        Assert.Equal(2 * 8 + 3, added["damage_+%"]);   // 2% per each of the 8 charges, 1% per power charge
        Assert.Equal(50, added["base_skill_area_of_effect_+%"]);   // 60%, up to 50%
        Assert.Equal(12, added["accuracy_rating_+%"]);
        // The charges add to the passives' base form, which the hits read; the virtual total shows both.
        Assert.Equal(8, added["base_additional_physical_damage_reduction_%"]);
        Assert.Equal(8 + 5, added["additional_physical_damage_reduction_%"]);
    }

    [Fact]
    public void No_charge_and_any_charge_conditions()
    {
        var get = Character(("damage_while_no_frenzy_charges_+%", 30), ("critical_strike_multiplier_+_while_have_any_frenzy_charges", 25),
            ("additional_maximum_all_resistances_%_with_no_endurance_charges", 2));
        var none = ChargeEffects.Expand(get, Held());
        Assert.Equal(30, none["damage_+%"]);
        Assert.Equal(2, none["additional_maximum_all_resistances_%"]);
        Assert.False(none.ContainsKey("base_critical_strike_multiplier_+"));
        var some = ChargeEffects.Expand(get, Held(endurance: 1, frenzy: 1));
        Assert.False(some.ContainsKey("damage_+%"));
        Assert.False(some.ContainsKey("additional_maximum_all_resistances_%"));
        Assert.Equal(25, some["base_critical_strike_multiplier_+"]);
    }

    [Fact]
    public void The_counts_go_to_the_panel_rows()
    {
        var limits = new Charges.Limits(5, 1, 10_000);
        var display = ChargeEffects.Display(Held(endurance: 3, frenzy: 1, power: 0), _ => limits).ToDictionary(d => d.Stat, d => d.Value);
        Assert.Equal(3, display["current_endurance_charges"]);
        Assert.Equal(3, display["actual_current_endurance_charges"]);
        Assert.Equal(1, display["minimum_endurance_charges"]);
        Assert.Equal(2, display["current_removable_endurance_charges"]);
        Assert.Equal(0, display["current_removable_frenzy_charges"]);
        Assert.Equal(0, display["current_power_charges"]);
        Assert.Equal(12, display.Count);
    }

    [Fact]
    public void Frenzy_charges_are_more_damage_for_hits_and_power_charges_for_spells_with_the_spell_stat()
    {
        Assert.Equal(1.0, ChargeEffects.DamageMore(With(Character(), Held())), 9);
        Assert.Equal(1.12, ChargeEffects.DamageMore(With(Character(), Held(frenzy: 3))), 9);
        var spell = With(Character(("object_inherent_spell_damage_+%_final_per_power_charge", 5)), Held(frenzy: 3, power: 2));
        Assert.Equal(1.12, ChargeEffects.DamageMore(spell), 9);
        Assert.Equal(1.12 * 1.10, ChargeEffects.DamageMore(spell, spell: true), 9);
        var quietMight = With(Character(("quiet_might_keystone_spell_damage_+%_final_per_power_charge", 3)), Held(power: 3));
        Assert.Equal(1.09, ChargeEffects.DamageMore(quietMight, spell: true), 9);
        Assert.Equal(1.0, ChargeEffects.DamageMore(quietMight), 9);
    }

    [Fact]
    public void Spell_damage_components_are_expanded_before_the_final_more_multiplier()
    {
        // Fireball's 9-14 with 3 Frenzy Charges: 4% more each (Character.ot), 12% more.
        var get = With(Character(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14)), Held(frenzy: 3));
        var (min, max) = SpellDamage.Hit(get);
        // The shared spell calculator now consumes charge more itself; a caller must not multiply it again.
        Assert.Equal(9 * 1.12, min, 6);
        Assert.Equal(14 * 1.12, max, 6);
        // Added spell damage per Power Charge counts too.
        var cold = With(Character(("spell_minimum_added_cold_damage_per_power_charge", 2), ("spell_maximum_added_cold_damage_per_power_charge", 4)), Held(power: 3));
        Assert.Equal((6.0, 12.0), SpellDamage.Hit(cold));
    }

    [Fact]
    public void Reductions_cap_at_ninety()
    {
        Assert.Equal((12, 12), ChargeEffects.Reduction(With(Character(), Held(endurance: 3))));
        var many = With(Character(("physical_damage_reduction_%_per_endurance_charge", 40)), Held(endurance: 3));
        Assert.Equal((90, 12), ChargeEffects.Reduction(many));
        Assert.Equal((0, 0), ChargeEffects.Reduction(With(Character(), Held())));
    }

    [Fact]
    public void Expanded_stats_feed_the_panel_regeneration_and_area()
    {
        var get = With(Character(("life_regeneration_rate_per_minute_%_per_endurance_charge", 60), ("projectile_speed_+%_per_frenzy_charge", 5),
            ("skill_area_of_effect_+%_per_power_charge", 10), ("base_projectile_speed_+%", 10)), Held(endurance: 2, frenzy: 3, power: 3));
        var panel = PanelStats.Compute(get).ToDictionary(p => p.Stat, p => p.Value);
        Assert.Equal(12, panel["cast_speed_+%"]);
        Assert.Equal(25, panel["projectile_speed_+%"]);
        Assert.Equal(4, panel["attack_speed_+%_per_frenzy_charge"]);
        // 1% of maximum life per second per Endurance Charge: 2% of 1000 life is 20 a second, 1200 a minute.
        Assert.Equal(1200, PanelStats.LifePerMinute(get, 1000), 6);
        // 30% increased area: the radius grows by floor(100 × √1.3) / 100.
        Assert.Equal(SkillArea.Radius(10, n => n == "base_skill_area_of_effect_+%" ? 30 : 0, spell: true), SkillArea.Radius(10, get, spell: true));
        Assert.True(SkillArea.Radius(10, get, spell: true) > 10);
    }

    [Fact]
    public void Physical_hits_taken_lose_the_endurance_reduction()
    {
        var (physical, elemental) = ChargeEffects.Reduction(With(Character(), Held(endurance: 3)));
        Assert.Equal(880, DamageModel.MitigatePhysical(1000, 0, physical));
        Assert.Equal(220, DamageModel.Reduce(DamageModel.Mitigate(1000, 75), elemental));
    }
}

using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChargeEffectsTests
{
    static readonly Lazy<StatCatalog> Catalog = new(() => new StatCatalog(ChargePanelStatsTestsData.Folder));

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

    // The three conversion uniques' stats (Mods GainBrutalChargesInsteadOfEnduranceUnique__1 and the other two) and
    // Character.ot's per-special-charge coefficients (PanelStatDefaults).
    static Func<string, int> Converted(ChargeType type, params (string Name, int Value)[] stats) =>
        Character(stats.Concat(PanelStatDefaults.Inherent.Select(i => (i.Stat, i.Value)))
            .Append((Charges.ConversionFlag(type), 1)).ToArray());

    static Func<ChargeType, bool> Only(ChargeType type) => t => t == type;

    [Fact]
    public void The_conversion_stats_exist_in_Stats_dat_and_name_the_three_unique_mods()
    {
        foreach (var type in Charges.Types)
        {
            Assert.NotNull(Catalog.Value.Key(Charges.ConversionFlag(type)));
            Assert.NotNull(Catalog.Value.Key(Charges.MinimumConversionFlag(type)));
            Assert.NotNull(Catalog.Value.Key($"current_{Charges.SpecialName(type)}_charges"));
            Assert.NotNull(Catalog.Value.Key($"minimum_{Charges.SpecialName(type)}_charges"));
        }
        Assert.Equal("gain_brutal_charges_instead_of_endurance_charges", Charges.ConversionFlag(ChargeType.Endurance));
        Assert.Equal("modifiers_to_minimum_power_charges_instead_apply_to_absorption_charges", Charges.MinimumConversionFlag(ChargeType.Power));
        Assert.Equal(["brutal", "affliction", "absorption"], Charges.Types.Select(Charges.SpecialName));
        Assert.Equal(ChargeType.Frenzy, Charges.Parse("affliction"));
    }

    [Fact]
    public void Converted_endurance_charges_are_brutal_charges_with_triple_damage_and_stun_threshold_instead_of_reduction()
    {
        var get = Converted(ChargeType.Endurance, ("resist_all_elements_%_per_endurance_charge", 5),
            ("damage_+%_per_frenzy_power_or_endurance_charge", 2), ("additional_maximum_all_resistances_%_with_no_endurance_charges", 2));
        var added = ChargeEffects.Expand(get, Held(endurance: 3, frenzy: 1), Only(ChargeType.Endurance));
        Assert.Equal(9, added["base_chance_to_deal_triple_damage_%"]);
        Assert.Equal(30, added["stun_threshold_+%"]);
        // None of the Endurance Charge effects: not Character.ot's reductions, not the per-charge resistances, not the
        // "all charges" count (PoB's TotalCharges is the standard count), and "with no Endurance Charges" holds.
        Assert.False(added.ContainsKey("additional_physical_damage_reduction_%"));
        Assert.False(added.ContainsKey("additional_elemental_damage_reduction_%"));
        Assert.False(added.ContainsKey("base_resist_all_elements_%"));
        Assert.Equal(2, added["damage_+%"]);
        Assert.Equal(2, added["additional_maximum_all_resistances_%"]);
        // Without the flag the same character has the standard effects and no Brutal ones.
        var plain = ChargeEffects.Expand(get, Held(endurance: 3, frenzy: 1));
        Assert.Equal(12, plain["additional_physical_damage_reduction_%"]);
        Assert.Equal(8, plain["damage_+%"]);
        Assert.False(plain.ContainsKey("base_chance_to_deal_triple_damage_%"));
        Assert.False(plain.ContainsKey("stun_threshold_+%"));
    }

    [Fact]
    public void Converted_counts_show_on_the_special_rows_and_the_minimum_follows_its_flag()
    {
        var limits = new Charges.Limits(5, 1, 10_000);
        var display = ChargeEffects.Display(Held(endurance: 3, frenzy: 2, power: 1), _ => limits, Only(ChargeType.Endurance), _ => false)
            .ToDictionary(d => d.Stat, d => d.Value);
        Assert.Equal(3, display["current_brutal_charges"]);
        Assert.Equal(0, display["minimum_brutal_charges"]);
        Assert.Equal(0, display["current_endurance_charges"]);
        Assert.Equal(0, display["actual_current_endurance_charges"]);
        Assert.Equal(0, display["current_removable_endurance_charges"]);
        Assert.Equal(2, display["current_frenzy_charges"]);
        Assert.Equal(0, display["current_affliction_charges"]);
        Assert.Equal(0, display["current_absorption_charges"]);
        Assert.Equal(18, display.Count);
        var minimum = ChargeEffects.Display(Held(endurance: 3), _ => limits, Only(ChargeType.Endurance), Only(ChargeType.Endurance))
            .ToDictionary(d => d.Stat, d => d.Value);
        Assert.Equal(1, minimum["minimum_brutal_charges"]);
        Assert.Equal(1, minimum["minimum_endurance_charges"]);
    }

    [Fact]
    public void Affliction_charges_are_more_damage_with_ailments_only()
    {
        var get = Converted(ChargeType.Frenzy);
        var added = ChargeEffects.Expand(get, Held(frenzy: 3), Only(ChargeType.Frenzy));
        Assert.False(added.ContainsKey("attack_speed_+%"));
        var display = ChargeEffects.Display(Held(frenzy: 3), _ => new Charges.Limits(3, 0, 10_000), Only(ChargeType.Frenzy))
            .ToDictionary(d => d.Stat, d => d.Value);
        int Final(string name) => get(name) + added.GetValueOrDefault(name) + display.GetValueOrDefault(name);
        Assert.Equal(1.0, ChargeEffects.DamageMore(Final), 9);   // not Frenzy's 4% more per charge: there are none
        Assert.Equal(1.0, ChargeEffects.DamageMore(Final, spell: true), 9);
        Assert.Equal(1.24, ChargeEffects.DamageMore(Final, ailment: true), 9);   // 8% per Affliction Charge
        Assert.Equal(1.12 * 1.24, ChargeEffects.DamageMore(With(Character(("ailment_damage_+%_final_per_affliction_charge", 8),
            ("current_affliction_charges", 3)), Held(frenzy: 3)), ailment: true), 9);
    }
}

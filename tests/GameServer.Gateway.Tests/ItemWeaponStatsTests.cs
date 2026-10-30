using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ItemWeaponStatsTests
{
    static readonly ModCatalog Mods = Program.Items.Mods;
    static ItemCatalog.BaseType Wand => Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!;
    static (int AttackMs, int Min, int Max, int Range) Basis => Program.Skills.WeaponBase(Wand.Row)!.Value;
    static Item Weapon(byte quality = 0, params ItemMod[] mods) => ItemCatalog.Create(Wand) with { Quality = quality, Mods = mods };

    // Use real Mods/Stats row shapes. Zero unrelated values when isolating one input; the complete paired native
    // elemental/no-physical row has a separate regression below. Out-of-roll signed/extreme values test numeric bounds.
    internal static ItemMod Mod(params (string Stat, int Value)[] values)
    {
        var row = Mods.All.First(m => values.All(v => m.Stats.Any(s => s.Id == v.Stat && s.Local)));
        return new(ModSection.Explicit, row.Hash,
            row.Stats.Select(s => values.FirstOrDefault(v => v.Stat == s.Id).Value).ToArray());
    }

    static ItemWeaponStats.Result Calculate(Item item) => ItemWeaponStats.Calculate(item, Basis, Mods);

    [Fact]
    public void Native_Driftwood_Wand_without_mods_retains_its_extracted_range_and_duration()
    {
        Assert.Equal((667, 5, 9, 120), Basis);
        var result = Calculate(Weapon());
        Assert.Equal(new DamageRange(5, 9), result.Damage[DamageType.Physical]);
        Assert.Equal(667, result.AttackMs);
        Assert.Single(result.Damage.Components);
    }

    [Fact]
    public void Physical_flat_increase_and_quality_multiply_before_one_half_up_item_round()
    {
        var result = Calculate(Weapon(20,
            Mod(("local_minimum_added_physical_damage", 15), ("local_maximum_added_physical_damage", 30)),
            Mod(("local_physical_damage_+%", 50))));

        // (5+15)*1.5*1.2=36 and (9+30)*1.5*1.2=70.2. Adding quality to the 50% bucket gives 34/66 instead.
        Assert.Equal(new DamageRange(36, 70), result.Damage[DamageType.Physical]);
        Assert.Equal(667, result.AttackMs);
    }

    [Fact]
    public void Fractional_local_physical_damage_rounds_halfway_up_instead_of_to_even()
    {
        var result = Calculate(Weapon(0, Mod(("local_physical_damage_+%", 70))));
        Assert.Equal(new DamageRange(9, 15), result.Damage[DamageType.Physical]); // 8.5 and 15.3; to-even would start at 8.
    }

    [Fact]
    public void Local_quality_adds_actual_quality_while_a_maximum_quality_cap_does_not()
    {
        var result = Calculate(Weapon(20, Mod(("local_item_quality_+", 10)), Mod(("local_maximum_quality_+", 25))));
        Assert.Equal(new DamageRange(7, 12), result.Damage[DamageType.Physical]); // 5/9 * 1.30, not * 1.55.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quality_prohibition_removes_all_physical_quality_but_keeps_local_increases(bool unidentified)
    {
        var item = Weapon(20, Mod(("local_item_quality_+", 10)),
            Mod(("local_quality_does_not_increase_physical_damage", 1)), Mod(("local_physical_damage_+%", 50)))
            with { Identified = !unidentified };
        Assert.Equal(new DamageRange(8, 14), Calculate(item).Damage[DamageType.Physical]);
    }

    [Fact]
    public void Each_local_damage_type_keeps_its_range_and_elemental_increases_exclude_chaos()
    {
        var item = Weapon(20,
            Mod(("local_physical_damage_+%", 100)),
            Mod(("local_minimum_added_fire_damage", 1), ("local_maximum_added_fire_damage", 3)),
            Mod(("local_minimum_added_cold_damage", 2), ("local_maximum_added_cold_damage", 4)),
            Mod(("local_minimum_added_lightning_damage", 3), ("local_maximum_added_lightning_damage", 5)),
            Mod(("local_minimum_added_chaos_damage", 4), ("local_maximum_added_chaos_damage", 6)),
            Mod(("local_elemental_damage_+%", 50)));
        var hit = Calculate(item).Damage;

        Assert.Equal(new DamageRange(12, 22), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(2, 5), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(3, 6), hit[DamageType.Cold]);
        Assert.Equal(new DamageRange(5, 8), hit[DamageType.Lightning]);
        Assert.Equal(new DamageRange(4, 6), hit[DamageType.Chaos]);
    }

    [Fact]
    public void Complete_native_elemental_enchant_removes_weapon_physical_without_removing_its_elements()
    {
        var row = Assert.IsType<ModCatalog.Mod>(Mods.ById("VillageLocalElementalDamageNoPhysical"));
        Assert.Equal(new[] { "local_elemental_damage_+%", "local_weapon_no_physical_damage" }, row.Stats.Select(s => s.Id));
        var item = Weapon(20, new(ModSection.Enchant, row.Hash, [50, 1]),
            Mod(("local_minimum_added_fire_damage", 2), ("local_maximum_added_fire_damage", 4)));

        Assert.Equal(DamageRange.Zero, Calculate(item).Damage[DamageType.Physical]);
        Assert.Equal(new DamageRange(3, 6), Calculate(item).Damage[DamageType.Fire]);
    }

    [Fact]
    public void Global_physical_and_attack_modifiers_are_left_for_the_character_pipeline()
    {
        var row = Mods.All.First(m => m.Stats.Any(s => s.Id == "physical_damage_+%" && !s.Local));
        var item = Weapon(0, new ItemMod(ModSection.Explicit, row.Hash,
            row.Stats.Select(s => s.Id == "physical_damage_+%" ? 100 : 0).ToArray()));
        Assert.Equal(new DamageRange(5, 9), Calculate(item).Damage[DamageType.Physical]);
    }

    [Fact]
    public void Every_native_mod_section_contributes_to_the_same_local_item_value()
    {
        var mod = Mod(("local_physical_damage_+%", 10));
        var item = Weapon(0, Enum.GetValues<ModSection>().Select(section => mod with { Section = section }).ToArray())
            with { Identified = false };
        Assert.Equal(new DamageRange(8, 14), Calculate(item).Damage[DamageType.Physical]);
    }

    [Theory]
    [InlineData(0, 667)]
    [InlineData(20, 556)]
    [InlineData(100, 334)]
    [InlineData(-50, 1334)]
    [InlineData(-100, 66700)]
    [InlineData(int.MaxValue, 1)]
    public void Local_attack_speed_changes_only_the_item_duration_with_existing_integer_ms_rounding(int speed, int expected)
    {
        var result = Calculate(Weapon(20, Mod(("local_attack_speed_+%", speed))));
        Assert.Equal(expected, result.AttackMs);
        Assert.Equal(new DamageRange(6, 11), result.Damage[DamageType.Physical]);
    }

    [Fact]
    public void Signed_local_sources_cancel_before_saturation_and_completed_ranges_stay_nonnegative()
    {
        var item = Weapon(0,
            Mod(("local_physical_damage_+%", int.MaxValue)),
            Mod(("local_physical_damage_+%", int.MaxValue)),
            Mod(("local_physical_damage_+%", int.MinValue)),
            Mod(("local_physical_damage_+%", int.MinValue)),
            Mod(("local_minimum_added_fire_damage", -5), ("local_maximum_added_fire_damage", -1)));
        Assert.Equal(new DamageRange(5, 9), Calculate(item).Damage[DamageType.Physical]); // -2% => 4.9/8.82.
        Assert.Equal(DamageRange.Zero, Calculate(item).Damage[DamageType.Fire]);
    }

    [Fact]
    public void Large_completed_values_saturate_and_reduced_ranges_do_not_wrap_or_invert()
    {
        var item = Weapon(255, Mod(("local_minimum_added_physical_damage", int.MaxValue),
            ("local_maximum_added_physical_damage", int.MaxValue)), Mod(("local_physical_damage_+%", int.MaxValue)));
        Assert.Equal(new DamageRange(int.MaxValue, int.MaxValue), Calculate(item).Damage[DamageType.Physical]);
        item = Weapon(0, Mod(("local_physical_damage_+%", -101)));
        Assert.Equal(DamageRange.Zero, Calculate(item).Damage[DamageType.Physical]);
        item = Weapon(0, Mod(("local_minimum_added_fire_damage", 9), ("local_maximum_added_fire_damage", 2)));
        Assert.Equal(new DamageRange(9, 9), Calculate(item).Damage[DamageType.Fire]);
    }
}

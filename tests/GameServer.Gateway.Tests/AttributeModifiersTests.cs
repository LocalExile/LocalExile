namespace GameServer.Gateway.Tests;

public class AttributeModifiersTests
{
    // PoB 16de4b8 CalcPerform:405 and Common:683: nearest integer, positive halves up, then nonnegative.
    [Theory]
    [InlineData(17, 3, 18)]
    [InlineData(49, 1, 49)]
    [InlineData(50, 1, 51)]
    [InlineData(51, 1, 52)]
    [InlineData(1, -50, 1)]
    [InlineData(3, -50, 2)]
    [InlineData(50, -1, 50)]
    [InlineData(51, -1, 50)]
    [InlineData(10, -100, 0)]
    [InlineData(10, -101, 0)]
    [InlineData(-10, 25, 0)]
    [InlineData(-10, -150, 5)] // Pinned PoB does not separately floor either signed bucket or the scale factor.
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MinValue, 0)]
    [InlineData(0, int.MaxValue, 0)]
    public void Rounding_reductions_and_saturation(int flat, int increased, int expected)
    {
        Assert.Equal(expected, new AttributeModifiers(Strength: flat, StrengthPct: increased).Apply().Strength);
    }

    [Fact]
    public void Signed_aliases_cancel_before_the_completed_buckets_saturate()
    {
        var raw = new Dictionary<string, int>
        {
            ["base_strength"] = int.MaxValue, ["additional_strength"] = int.MaxValue,
            ["additional_all_attributes"] = -int.MaxValue, ["base_strength_and_dexterity"] = -int.MaxValue,
            ["strength_+%"] = int.MaxValue, ["dexterity_+%"] = int.MaxValue,
            ["all_attributes_+%"] = -int.MaxValue,
            ["base_intelligence"] = int.MaxValue, ["additional_intelligence"] = int.MaxValue
        };
        var result = AttributeModifiers.From(n => raw.GetValueOrDefault(n)).Apply(17, 19, 23);
        Assert.Equal((17, 0, 0), result);
        raw["all_attributes_+%"] = 0;
        raw["strength_+%"] = 0;
        Assert.Equal((17, 0, int.MaxValue), AttributeModifiers.From(n => raw.GetValueOrDefault(n)).Apply(17, 19, 23));
    }

    [Fact]
    public void Extreme_flat_and_increased_aliases_saturate_instead_of_wrapping()
    {
        int Positive(string name) => name == "intelligence_is_0" ? 0 : int.MaxValue;
        Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue), AttributeModifiers.From(Positive).Apply(14, 14, 32));
        var negativeFlat = AttributeModifiers.From(n => n.EndsWith("_+%") ? 0 : int.MinValue);
        Assert.Equal((0, 0, 0), negativeFlat.Apply(14, 14, 32));
    }

    [Fact]
    public void Zero_increases_preserve_odd_attributes_and_existing_pool_rounding()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 20,
            new(Strength: 3, Dexterity: 5, Intelligence: 1, Life: 7, LifePct: 13, Mana: 9, ManaPct: 17,
                Accuracy: 11, AccuracyPct: 13, Evasion: 23, EvasionPct: 7, EnergyShield: 100,
                EnergyShieldPct: 8, MeleePhysicalDamagePct: 9));
        Assert.Equal((17, 19, 33), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(331u, pools.Life);
        Assert.Equal(209u, pools.Mana);
        Assert.Equal(100, pools.Accuracy);
        Assert.Equal(41, pools.Evasion);
        Assert.Equal(10, pools.EvasionPct);
        Assert.Equal(14, pools.EnergyShieldPct);
        Assert.Equal(114u, pools.EnergyShield);
        Assert.Equal(12, pools.MeleePhysicalDamagePct);
    }

    [Theory]
    [InlineData(3, 15, 118u)] // 15.45 rounds down: three complete Strength groups.
    [InlineData(4, 16, 129u)] // 15.60 rounds up across the next group boundary.
    [InlineData(49, 22, 139u)]
    [InlineData(50, 23, 139u)] // 22.5 rounds up; the remainder still grants no mana.
    [InlineData(60, 24, 150u)]
    [InlineData(-100, 0, 86u)]
    public void Final_attribute_rounding_precedes_Strength_groups_and_mana_truncation(
        int increase, int strength, uint mana)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Intelligence = 33 };
        var bonus = new CharacterStats.Bonus(Mana: 9, ManaPct: 33, ManaPerFourStrength: 8,
            Attributes: new(Strength: 1, StrengthPct: increase));
        var pools = CharacterStats.For(c, 1, bonus);
        Assert.Equal(strength, pools.Strength);
        Assert.Equal(33, pools.Intelligence); // Odd Intelligence still uses integer / 2 before mana increases.
        Assert.Equal(mana, pools.Mana);
        var inoculated = CharacterStats.For(c, 1, bonus with { ChaosInoculation = true, ArmourAppliesToChaosDamage = true });
        Assert.Equal(1u, inoculated.Life);
        Assert.Equal(mana, inoculated.Mana);
        Assert.True(inoculated.ArmourAppliesToChaosDamage);
    }

    [Fact]
    public void Saturated_final_attributes_remain_safe_in_life_mana_and_accuracy()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new(Attributes: new(int.MaxValue, int.MaxValue, int.MaxValue, 100, 100, 100)));
        Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(1073741873u, pools.Life);
        Assert.Equal(1073741863u, pools.Mana);
        Assert.Equal(int.MaxValue, pools.Accuracy);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void Increased_life_and_mana_pools_fit_the_signed_publication_and_reservation_range(int increased)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new(LifePct: increased, ManaPct: increased,
                Attributes: new(Strength: int.MaxValue, Intelligence: int.MaxValue)));
        Assert.Equal((uint)int.MaxValue, pools.Life);
        Assert.Equal((uint)int.MaxValue, pools.Mana);
        AuraCatalog.Aura[] auras = [new(0x103, 0, "Haste", "Haste", 1, 0, 0, 5000)];
        Assert.Equal(1073741824, AuraCatalog.ReservedMana(pools, auras));
        Assert.Equal(1073741823, AuraCatalog.UnreservedMana(pools, auras));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_flat_and_increased_defences_bound_final_ratings_after_the_Iron_Reflexes_sum(bool ironReflexes)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new(Armour: int.MaxValue, ArmourPct: 100, Evasion: int.MaxValue, EvasionPct: int.MaxValue,
                ColdOvercapEvasionPct: int.MaxValue, IronReflexes: ironReflexes,
                Attributes: new(Dexterity: int.MaxValue)));
        Assert.Equal(int.MaxValue, pools.Armour);
        Assert.Equal(ironReflexes ? 0 : int.MaxValue, pools.Evasion);
        Assert.Equal(int.MaxValue, pools.EvasionPct);
    }

    [Fact]
    public void Attribute_derived_increases_stay_wide_until_final_ES_and_melee_values_are_bounded()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new(EnergyShield: 14, EnergyShieldPct: int.MaxValue, MeleePhysicalDamagePct: int.MaxValue,
                Attributes: new(Strength: int.MaxValue, Intelligence: int.MaxValue)));
        Assert.Equal(int.MaxValue, pools.EnergyShieldPct);
        Assert.Equal(360777266u, pools.EnergyShield); // Keeps the full 2576980376% increase when scaling 14 flat ES.
        Assert.Equal(int.MaxValue, pools.MeleePhysicalDamagePct);
    }

    [Fact]
    public void A_complete_flat_bucket_above_the_native_range_is_scaled_before_the_final_clamp()
    {
        Assert.Equal(1073741831, new AttributeModifiers(Strength: (long)int.MaxValue + 14, StrengthPct: -50)
            .Apply().Strength);
        Assert.Equal(int.MaxValue, new AttributeModifiers(Strength: long.MaxValue, StrengthPct: long.MaxValue)
            .Apply().Strength);
        Assert.Equal(int.MaxValue, new AttributeModifiers(Strength: long.MinValue, StrengthPct: long.MinValue)
            .Apply().Strength);
    }
}

namespace GameServer.Gateway.Tests;

// Pure calculator isolation; native catalog, admission and command/wire controls live in the Gateway tests.
public sealed class AttributeDefenceSourceTests
{
    static SkillCatalog.ClassInfo Attributes(int strength = 0, int dexterity = 0, int intelligence = 0) =>
        SkillCatalog.ClassInfo.Witch with { Strength = strength, Dexterity = dexterity, Intelligence = intelligence };

    [Theory]
    [InlineData(0, 100u, 0)]
    [InlineData(9, 100u, 0)]
    [InlineData(10, 101u, 1)]
    [InlineData(19, 101u, 1)]
    [InlineData(20, 102u, 2)]
    public void Attribute_defence_ES_uses_only_completed_final_Strength_groups(
        int strength, uint expectedEnergyShield, int expectedIncrease)
    {
        var pools = CharacterStats.For(Attributes(strength: strength), 1,
            new(EnergyShield: 100, EnergyShieldPctPerTenStrength: 1));

        Assert.Equal(strength, pools.Strength);
        Assert.Equal(expectedIncrease, pools.EnergyShieldPct);
        Assert.Equal(expectedEnergyShield, pools.EnergyShield);
        Assert.Equal(15, pools.Evasion);
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(9, 100, 0)]
    [InlineData(10, 102, 2)]
    [InlineData(19, 102, 2)]
    [InlineData(20, 104, 4)]
    public void Attribute_defence_EV_uses_only_completed_final_Intelligence_groups(
        int intelligence, int expectedEvasion, int expectedIncrease)
    {
        var pools = CharacterStats.For(Attributes(intelligence: intelligence), 1,
            new(Evasion: 85, EvasionPctPerTenIntelligence: 2));

        Assert.Equal(intelligence, pools.Intelligence);
        Assert.Equal(expectedIncrease, pools.EvasionPct);
        Assert.Equal(expectedEvasion, pools.Evasion);
        Assert.Equal(0u, pools.EnergyShield);
    }

    [Fact]
    public void Attribute_defence_group_inputs_follow_flat_typed_and_all_attribute_increases()
    {
        var pools = CharacterStats.For(Attributes(strength: 9, intelligence: 9), 1,
            new(Strength: 1, Intelligence: 1, StrengthPct: 75, IntelligencePct: 75, AllAttributesPct: 25,
                EnergyShield: 100, Evasion: 85,
                EnergyShieldPctPerTenStrength: 1, EvasionPctPerTenIntelligence: 2));

        Assert.Equal((20, 0, 20), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(4, pools.EnergyShieldPct); // Int20/10 plus two completed Strength groups.
        Assert.Equal(104u, pools.EnergyShield);
        Assert.Equal(4, pools.EvasionPct);
        Assert.Equal(104, pools.Evasion);
    }

    [Fact]
    public void Attribute_defence_native_coefficients_add_to_existing_inherent_buckets_without_cross_pool_leakage()
    {
        var attributes = Attributes(100, 100, 100);
        var source = new CharacterStats.Bonus(EnergyShield: 100, Evasion: 85);
        var baseline = CharacterStats.For(attributes, 1, source);
        var added = CharacterStats.For(attributes, 1, source with
        {
            EnergyShieldPctPerTenStrength = 1,
            EvasionPctPerTenIntelligence = 2,
        });

        // Native IntelligenceDescription and pinned PoB give one ES INC per ten Intelligence.
        Assert.Equal((110u, 120, 10, 20),
            (baseline.EnergyShield, baseline.Evasion, baseline.EnergyShieldPct, baseline.EvasionPct));
        Assert.Equal((120u, 140, 20, 40),
            (added.EnergyShield, added.Evasion, added.EnergyShieldPct, added.EvasionPct));
        Assert.Equal((baseline.Life, baseline.Mana, baseline.Accuracy, baseline.Armour),
            (added.Life, added.Mana, added.Accuracy, added.Armour));
    }

    [Theory]
    [InlineData(0, 0, 110u, 100, 10, 0)]
    [InlineData(-1, -2, 100u, 80, 0, -20)]
    [InlineData(-12, -10, 0u, 0, -110, -100)]
    [InlineData(-13, -11, 0u, 0, -120, -110)]
    public void Attribute_defence_signed_coefficients_are_INC_and_only_completed_rating_factors_floor_at_zero(
        int energyShieldCoefficient, int evasionCoefficient, uint expectedEnergyShield, int expectedEvasion,
        int expectedEnergyShieldIncrease, int expectedEvasionIncrease)
    {
        var pools = CharacterStats.For(Attributes(strength: 100, intelligence: 100), 1,
            new(EnergyShield: 100, Evasion: 85,
                EnergyShieldPctPerTenStrength: energyShieldCoefficient,
                EvasionPctPerTenIntelligence: evasionCoefficient));

        Assert.Equal(expectedEnergyShieldIncrease, pools.EnergyShieldPct);
        Assert.Equal(expectedEvasionIncrease, pools.EvasionPct);
        Assert.Equal(expectedEnergyShield, pools.EnergyShield);
        Assert.Equal(expectedEvasion, pools.Evasion);
    }

    [Fact]
    public void Attribute_defence_explicit_zero_preserves_existing_defence_and_unrelated_pool_policies()
    {
        var source = new CharacterStats.Bonus(EnergyShield: 100, Evasion: 85, Armour: 37,
            EnergyShieldPct: 13, EvasionPct: 17, ColdOvercapEvasionPct: 11,
            ArmourMorePct: 10, EvasionMorePct: 20, IronReflexes: true,
            Life: 7, Mana: 9, LifePct: 25, ManaPct: 30, ManaPerFourStrength: 8);
        var baseline = CharacterStats.For(Attributes(100, 50, 100), 10, source);
        var zero = CharacterStats.For(Attributes(100, 50, 100), 10, source with
        {
            EnergyShieldPctPerTenStrength = 0,
            EvasionPctPerTenIntelligence = 0,
        });

        Assert.Equal(baseline, zero);
    }

    static CharacterStats.Bonus SlotSources() => new(
        Armour: 200, Evasion: 285, EnergyShield: 300,
        ArmourPct: 20, EvasionPct: 20, EnergyShieldPct: 20, SharedDefencesPct: 20,
        ShieldArmour: 100, ShieldEvasion: 100, ShieldEnergyShield: 100,
        ShieldArmourPct: 20, ShieldEvasionPct: 30, ShieldEnergyShieldPct: 40, SharedShieldDefencesPct: 50,
        BodyArmour: 100, BodyEvasion: 100, BodyEnergyShield: 100, BodyEnergyShieldPct: 30,
        EnergyShieldPctPerTenStrength: 1, EvasionPctPerTenIntelligence: 2);

    [Theory]
    [InlineData(false, false, false, false, 310, 530, 540u, 50)]
    [InlineData(true, false, false, false, 830, 0, 540u, 40)]
    [InlineData(false, true, false, false, 120, 300, 310u, 50)]
    [InlineData(true, true, false, false, 400, 0, 310u, 40)]
    [InlineData(true, false, true, false, 1090, 0, 540u, 40)]
    [InlineData(true, false, false, true, 570, 0, 540u, 40)]
    public void Attribute_defence_slot_and_Iron_Reflexes_matrix_retains_each_global_and_slot_term_once(
        bool ironReflexes, bool noShield, bool doubleBody, bool noBody,
        int expectedArmour, int expectedEvasion, uint expectedEnergyShield, int expectedEvasionIncrease)
    {
        var bonus = SlotSources() with
        {
            IronReflexes = ironReflexes,
            NoShieldDefences = noShield,
            DoubleBodyArmour = doubleBody,
            NoBodyArmour = noBody,
        };
        var pools = CharacterStats.For(Attributes(100, 50, 100), 1, bonus);

        // Normal EV530 = independent150 + body150 + shield230; ES540 = 140 + 170 + 230.
        // IR converted EV520 = independent140 + body140 + shield240; Armour310 is then added.
        // Only inherent Dexterity's10% is excluded; the Intelligence-derived20% remains in converted EV.
        Assert.Equal(expectedArmour, pools.Armour);
        Assert.Equal(expectedEvasion, pools.Evasion);
        Assert.Equal(expectedEnergyShield, pools.EnergyShield);
        Assert.Equal(expectedEvasionIncrease, pools.EvasionPct);
        Assert.Equal(40, pools.EnergyShieldPct);
    }

    [Theory]
    [InlineData(false, 143, 210, 75)]
    [InlineData(true, 374, 0, 65)]
    public void Attribute_defence_cold_overcap_is_additive_and_MORE_scales_the_completed_converted_bucket(
        bool ironReflexes, int expectedArmour, int expectedEvasion, int expectedEvasionIncrease)
    {
        var pools = CharacterStats.For(Attributes(100, 50, 100), 1,
            new(Armour: 100, ArmourPct: 30, Evasion: 85, EvasionPct: 20,
                SharedDefencesPct: 20, ColdOvercapEvasionPct: 25,
                ArmourMorePct: 10, EvasionMorePct: 20, IronReflexes: ironReflexes,
                EnergyShield: 100, EnergyShieldPct: 5,
                EnergyShieldPctPerTenStrength: 1, EvasionPctPerTenIntelligence: 2));

        // Normal EV100 * (1+.20+.25+.20+.10) * 1.20 =210; ordinary Armour100 * 1.30 * 1.10 =143.
        // IR adds100 * (1+.30+.20+.25+.20-.20) * 1.20 * 1.10 =231, without the Dex10%.
        Assert.Equal(expectedArmour, pools.Armour);
        Assert.Equal(expectedEvasion, pools.Evasion);
        Assert.Equal(expectedEvasionIncrease, pools.EvasionPct);
        Assert.Equal(125u, pools.EnergyShield);
    }

    [Fact]
    public void Attribute_defence_slot_fractions_are_summed_before_the_single_final_rating_round()
    {
        var pools = CharacterStats.For(Attributes(10, 0, 10), 1,
            new(EnergyShield: 3, EnergyShieldPct: 37, ShieldEnergyShield: 1, BodyEnergyShield: 1,
                Evasion: 5, EvasionPct: 2, ShieldEvasion: 10, BodyEvasion: 5,
                EnergyShieldPctPerTenStrength: 1, EvasionPctPerTenIntelligence: 2));

        Assert.Equal(4u, pools.EnergyShield); // Three1.39 contributions sum to4.17; separately rounding gives3.
        Assert.Equal(21, pools.Evasion); // Independent5.2 + body5.2 + shield10.4 =20.8, rather than20.
        Assert.Equal(39, pools.EnergyShieldPct);
        Assert.Equal(4, pools.EvasionPct);
    }

    [Fact]
    public void Attribute_defence_completed_ratings_keep_existing_halfway_up_projection()
    {
        var pools = CharacterStats.For(Attributes(10, 0, 10), 1,
            // Int10 contributes1 and Strength10 contributes1; -52 retains the exact -50% halfway factor.
            new(EnergyShield: 1, EnergyShieldPct: -52, EvasionPct: 8,
                EnergyShieldPctPerTenStrength: 1, EvasionPctPerTenIntelligence: 2));

        Assert.Equal(-50, pools.EnergyShieldPct);
        Assert.Equal(1u, pools.EnergyShield); // One ES * .50 =.5.
        Assert.Equal(10, pools.EvasionPct);
        Assert.Equal(17, pools.Evasion); // Inherent15 EV * 1.10 =16.5.
    }

    [Fact]
    public void Attribute_defence_wide_group_products_cancel_before_percentage_publication_bounds()
    {
        var pools = CharacterStats.For(Attributes(20, 0, 20), 1,
            new(EnergyShield: 1, EnergyShieldPct: int.MinValue + 2, EvasionPct: int.MinValue,
                EnergyShieldPctPerTenStrength: int.MaxValue, EvasionPctPerTenIntelligence: int.MaxValue));

        Assert.Equal(int.MaxValue, pools.EnergyShieldPct); // Wide ES INC2147483650 is bounded only for publication.
        Assert.Equal(21474838u, pools.EnergyShield); // 1 * (100+2147483650)/100 =21474837.5, rounding up.
        Assert.Equal(2147483646, pools.EvasionPct);
        Assert.Equal(322122562, pools.Evasion); // Inherent15 * (100+2147483646)/100 =322122561.9.
    }

    [Fact]
    public void Attribute_defence_negative_wide_global_INCs_can_be_rescued_by_shield_INCs_before_rating_clamp()
    {
        var pools = CharacterStats.For(Attributes(20, 0, 20), 1,
            new(EnergyShield: 100, ShieldEnergyShield: 100, EnergyShieldPct: int.MaxValue - 4,
                ShieldEnergyShieldPct: int.MaxValue, Evasion: 85, ShieldEvasion: 100,
                EvasionPct: int.MaxValue, ShieldEvasionPct: int.MaxValue,
                EnergyShieldPctPerTenStrength: int.MinValue, EvasionPctPerTenIntelligence: int.MinValue));

        // Wide global INCs are ES -2147483651 and EV -2147483649; shield2147483647 leaves -4%/-2%.
        // Prematurely clamping either global INC to int.MinValue would incorrectly give99 instead.
        Assert.Equal(int.MinValue, pools.EnergyShieldPct);
        Assert.Equal(int.MinValue, pools.EvasionPct);
        Assert.Equal(96u, pools.EnergyShield);
        Assert.Equal(98, pools.Evasion);
    }

    [Fact]
    public void Attribute_defence_IR_retains_above_bound_EV_increase_until_armour_bucket_cancellation()
    {
        var pools = CharacterStats.For(Attributes(intelligence: 20), 1,
            new(ArmourPct: int.MinValue, IronReflexes: true,
                EvasionPctPerTenIntelligence: int.MaxValue));

        // EV INC4294967294 plus Armour INC-2147483648 gives2147483646 on the15 converted base.
        Assert.Equal(int.MaxValue, pools.EvasionPct);
        Assert.Equal(322122562, pools.Armour);
        Assert.Equal(0, pools.Evasion);
    }

    [Fact]
    public void Attribute_defence_IR_retains_below_bound_EV_increase_until_armour_bucket_cancellation()
    {
        var pools = CharacterStats.For(Attributes(intelligence: 20), 1,
            new(Evasion: 85, EvasionPct: int.MaxValue, ArmourPct: int.MaxValue, IronReflexes: true,
                EvasionPctPerTenIntelligence: int.MinValue));

        // EV INC-2147483649 plus Armour INC2147483647 gives -2%, hence98 on converted base100.
        Assert.Equal(int.MinValue, pools.EvasionPct);
        Assert.Equal(98, pools.Armour);
        Assert.Equal(0, pools.Evasion);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, 2147483647u, 2147483647)]
    [InlineData(int.MinValue, int.MinValue, 0u, 0)]
    public void Attribute_defence_extreme_signed_products_reach_representable_rating_limits(
        int energyShieldCoefficient, int evasionCoefficient, uint expectedEnergyShield, int expectedEvasion)
    {
        var pools = CharacterStats.For(Attributes(int.MaxValue, int.MaxValue, int.MaxValue), 100,
            new(EnergyShield: int.MaxValue, Evasion: int.MaxValue,
                EnergyShieldPctPerTenStrength: energyShieldCoefficient,
                EvasionPctPerTenIntelligence: evasionCoefficient));

        Assert.Equal(expectedEnergyShield, pools.EnergyShield);
        Assert.Equal(expectedEvasion, pools.Evasion);
        Assert.Equal(energyShieldCoefficient, pools.EnergyShieldPct);
        Assert.Equal(evasionCoefficient, pools.EvasionPct);
    }
}

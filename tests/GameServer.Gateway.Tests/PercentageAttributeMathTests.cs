namespace GameServer.Gateway.Tests;

public sealed class PercentageAttributeMathTests
{
    [Fact]
    public void Typed_and_all_increases_add_on_the_whole_class_and_flat_bucket()
    {
        Assert.Equal((134, 116, 116), CharacterStats.ResolveAttributes((40, 40, 40), (60, 60, 60), (18, 0, 0), 16));
    }

    [Theory]
    [InlineData(149, 49)]
    [InlineData(150, 50)]
    [InlineData(151, 50)]
    [InlineData(154, 51)]
    public void Pinned_reference_half_up_projection_is_applied_once(int flat, int expected)
    {
        // At -67%,149/150/151/154 produce49.17/49.5/49.83/50.82. Native publication cannot prove GGG's rounding.
        Assert.Equal((expected, 0, 0), CharacterStats.ResolveAttributes((0, 0, 0), (flat, 0, 0), (-67, 0, 0)));
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(-101)]
    [InlineData(int.MinValue)]
    public void Reductions_cannot_publish_negative_attributes(int reduction)
    {
        Assert.Equal((0, 0, 0), CharacterStats.ResolveAttributes((14, 14, 32), (40, 40, 40), (0, 0, 0), reduction));
    }

    [Fact]
    public void Wide_intermediates_prevent_overflow_before_the_final_native_integer_clamp()
    {
        Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue), CharacterStats.ResolveAttributes(
            (int.MaxValue, int.MaxValue, int.MaxValue), (int.MaxValue, int.MaxValue, int.MaxValue),
            (int.MaxValue, int.MaxValue, int.MaxValue), int.MaxValue));
        Assert.Equal((0, 0, 0), CharacterStats.ResolveAttributes((0, 0, 0),
            (int.MinValue, int.MinValue, int.MinValue), (0, 0, 0)));
    }

    [Fact]
    public void Final_attributes_feed_all_existing_inherent_consumers_once()
    {
        var bonus = new CharacterStats.Bonus(Strength: 40, Dexterity: 40, Intelligence: 40,
            StrengthPct: 8, DexterityPct: 8, IntelligencePct: 8, AllAttributesPct: 10,
            EnergyShield: 100, ManaPerFourStrength: 8);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);
        Assert.Equal((64, 64, 85), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(1270u, pools.Life);
        Assert.Equal(804u, pools.Mana); //676 ordinary mana +16 complete groups of Strength *8.
        Assert.Equal(328, pools.Accuracy);
        Assert.Equal(12, pools.EvasionPct);
        Assert.Equal(17, pools.Evasion);
        Assert.Equal(8, pools.EnergyShieldPct); // Eight completed groups of final 85 Intelligence.
        Assert.Equal(108u, pools.EnergyShield);
        Assert.Equal(12, pools.MeleePhysicalDamagePct);
        Assert.True(pools.AttributesResolved);
    }

    [Fact]
    public void Extreme_final_attributes_do_not_wrap_the_dependent_mana_and_accuracy_buckets()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new(Strength: int.MaxValue, Dexterity: int.MaxValue, Intelligence: int.MaxValue, AllAttributesPct: int.MaxValue));
        Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(int.MaxValue, pools.Accuracy);
        Assert.Equal(1_073_742_457u, pools.Mana);
        Assert.True(pools.Life > 0);
        Assert.True(pools.Evasion >= 0);
    }

    [Fact]
    public void Calculated_zero_marker_survives_pool_clones()
    {
        var zero = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, new(AllAttributesPct: -100));
        Assert.Equal((0, 0, 0), (zero.Strength, zero.Dexterity, zero.Intelligence));
        Assert.True((zero with { AttackBlockLucky = true }).AttributesResolved);
        Assert.False(new CharacterStats.Pools(1, 50, 40, 0, 0).AttributesResolved);
    }
}

using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class DamageModelTests
{
    [Fact]
    public void Resistances_are_capped_and_floored()
    {
        Assert.Equal(75, DamageModel.Capped(90));
        Assert.Equal(75, DamageModel.Capped(75));
        Assert.Equal(0, DamageModel.Capped(0));
        Assert.Equal(-60, DamageModel.Capped(-60));
        Assert.Equal(-200, DamageModel.Capped(-300));
        Assert.Equal(90, DamageModel.Capped(100, 90));
        Assert.Equal(90, DamageModel.Capped(100, 200));
    }

    [Fact]
    public void A_hit_is_reduced_by_the_capped_resistance()
    {
        Assert.Equal(100, DamageModel.Mitigate(100, 0));
        Assert.Equal(50, DamageModel.Mitigate(100, 50));
        Assert.Equal(25, DamageModel.Mitigate(100, 75));
        Assert.Equal(25, DamageModel.Mitigate(100, 90));
        Assert.Equal(160, DamageModel.Mitigate(100, -60));
    }

    [Fact]
    public void Damage_over_time_keeps_its_fraction()
    {
        Assert.Equal(50.0, DamageModel.Mitigate(100.0, 50), 3);
        Assert.Equal(25.0, DamageModel.Mitigate(100.0, 75), 3);
        Assert.Equal(160.0, DamageModel.Mitigate(100.0, -60), 3);
    }

    [Fact]
    public void Spell_suppression_caps_at_100_and_reduces_by_40_percent()
    {
        Assert.True(DamageModel.Suppresses(100, 99));
        Assert.True(DamageModel.Suppresses(25, 24));
        Assert.False(DamageModel.Suppresses(25, 25));
        Assert.False(DamageModel.Suppresses(0, 0));
        Assert.Equal(60, DamageModel.Suppress(100));
        Assert.Equal(30, DamageModel.Suppress(50));
        Assert.Equal(50, DamageModel.Suppress(100, 50));
        Assert.Equal(0, DamageModel.Suppress(100, 100));
    }

    [Fact]
    public void Lucky_and_unlucky_suppression_roll_twice()
    {
        Assert.Equal(75.0, DamageModel.EffectiveSuppressionChance(50, lucky: true), 3);
        Assert.Equal(25.0, DamageModel.EffectiveSuppressionChance(50, unlucky: true), 3);
        Assert.Equal(100.0, DamageModel.EffectiveSuppressionChance(100, lucky: true), 3);
        Assert.Equal(0.0, DamageModel.EffectiveSuppressionChance(0, lucky: true), 3);
        Assert.True(DamageModel.Suppresses(50, 74, lucky: true));
        Assert.False(DamageModel.Suppresses(50, 75, lucky: true));
        Assert.True(DamageModel.Suppresses(50, 24, unlucky: true));
        Assert.False(DamageModel.Suppresses(50, 25, unlucky: true));
    }

    [Fact]
    public void Block_is_capped_at_90()
    {
        Assert.True(DamageModel.Blocks(75, 74.9));
        Assert.False(DamageModel.Blocks(75, 75.0));
        Assert.True(DamageModel.Blocks(100, 89.9));
        Assert.False(DamageModel.Blocks(100, 90.0));
        Assert.False(DamageModel.Blocks(0, 0));
    }

    [Fact]
    public void Lucky_block_keeps_the_better_of_two_rolls()
    {
        Assert.Equal(75.0, DamageModel.EffectiveBlockChance(50, lucky: true), 3);
        Assert.Equal(99.0, DamageModel.EffectiveBlockChance(90, lucky: true), 3);
        Assert.True(DamageModel.Blocks(50, 74.9, lucky: true));
        Assert.False(DamageModel.Blocks(50, 75.0, lucky: true));
    }

    [Theory]
    [InlineData(500, 0, 5)]
    [InlineData(500, 100, 10)]
    [InlineData(830, 50, 12.45)]
    [InlineData(0, 200, 0)]
    [InlineData(500, 10000, 100)]
    public void Critical_strike_chance_is_the_base_raised_by_increased(int basePerMyriad, int increased, double expected)
    {
        Assert.Equal(expected, DamageModel.CriticalStrikeChance(basePerMyriad, increased), 2);
    }

    [Theory]
    [InlineData(10, 150, 15)]
    [InlineData(10, 200, 20)]
    [InlineData(7, 175, 12)]
    [InlineData(0, 200, 0)]
    public void Critical_damage_uses_the_multiplier(int damage, int multiplier, int expected)
    {
        Assert.Equal(expected, DamageModel.CriticalDamage(damage, multiplier));
    }

    [Fact]
    public void Accuracy_hit_chance_matches_PoB()
    {
        // PoB CalcDefence: accuracy / (accuracy + (evasion / 5)^0.9) * 125, rounded and clamped to 5..100.
        Assert.Equal(93, DamageModel.AccuracyHitChance(67, 30));
        Assert.Equal(100, DamageModel.AccuracyHitChance(0, 30));
        Assert.Equal(5, DamageModel.AccuracyHitChance(67, 0));
        Assert.Equal(56, DamageModel.AccuracyHitChance(13445, 1000));
    }

    [Fact]
    public void An_attack_hits_when_the_roll_is_below_its_chance()
    {
        Assert.True(DamageModel.Hits(93, 92));
        Assert.False(DamageModel.Hits(93, 93));
        Assert.True(DamageModel.Hits(5, 4));
        Assert.False(DamageModel.Hits(5, 5));
    }

    [Fact]
    public void Armour_reduction_matches_PoB_and_is_capped()
    {
        // PoB CalcDefence: armour / (armour + damage * 5) * 100, rounded, capped at 90%.
        Assert.Equal(17, DamageModel.ArmourReduction(1000, 1000));
        Assert.Equal(0, DamageModel.ArmourReduction(0, 1000));
        Assert.Equal(0, DamageModel.ArmourReduction(1000, 0));
        Assert.Equal(90, DamageModel.ArmourReduction(1_000_000, 1));
        Assert.Equal(830, DamageModel.MitigatePhysical(1000, 1000));
        Assert.Equal(730, DamageModel.MitigatePhysical(1000, 1000, 10));
        Assert.Equal(100, DamageModel.MitigatePhysical(1000, 1000, 100));
        Assert.Equal(0, DamageModel.MitigatePhysical(0, 1000));
    }

    [Fact]
    public void Evasion_is_the_missing_part_of_the_attack_hit_chance()
    {
        Assert.Equal(7, DamageModel.EvadeChance(67, 30));
        Assert.Equal(95, DamageModel.EvadeChance(1_000_000, 30));
        Assert.Equal(95, DamageModel.EvadeChance(67, 0));
    }
}

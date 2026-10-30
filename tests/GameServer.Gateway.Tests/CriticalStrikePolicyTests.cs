namespace GameServer.Gateway.Tests;

public class CriticalStrikePolicyTests
{
    [Theory]
    [InlineData(0, 300, 1, 1)]
    [InlineData(10000, 300, -1, 3)]
    [InlineData(5000, 200, 0, 1.5)]
    [InlineData(5000, 200, 1, 1.75)]
    [InlineData(5000, 200, -1, 1.25)]
    [InlineData(2500, 300, 1, 1.875)]
    [InlineData(2500, 300, -1, 1.125)]
    [InlineData(5000, -100, 0, .5)]
    [InlineData(-100, 300, 0, 1)]
    [InlineData(20000, 300, 0, 3)]
    public void Expected_damage_accounts_for_ordinary_lucky_and_unlucky_critical_outcomes(
        int chance, int multiplier, int extra, double expected) =>
        Assert.Equal(expected, CriticalStrikePolicy.DamageFactor(chance, multiplier, extra), 10);

    [Theory]
    [InlineData(5000, 300, 0, 50, .75)]
    [InlineData(5000, 300, 1, 50, .875)]
    [InlineData(5000, 300, -1, 50, .625)]
    [InlineData(10000, 300, 1, 50, 1)]
    [InlineData(0, 300, 1, 50, .5)]
    [InlineData(5000, -100, 0, 50, .375)]
    [InlineData(5000, 300, 1, -100, 0)]
    [InlineData(5000, 300, 1, 200, 2.5)]
    public void Attack_estimates_apply_hit_probability_twice_only_to_the_critical_contribution(
        int chance, int multiplier, int luck, int hitChance, double expected) =>
        Assert.Equal(expected, CriticalStrikePolicy.DamageFactor(chance, multiplier, luck, hitChance), 10);

    sealed class CountRandom(params int[] values) : Random
    {
        public int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Assert.Equal(10_000, maxValue);
            Assert.True(Calls < values.Length, "Unexpected extra critical draw");
            return values[Calls++];
        }
    }

    [Theory]
    [InlineData(50u, 100u, true)]
    public void Living_low_life_uses_the_inclusive_maximum_life_boundary(uint life, uint maximum, bool expected)
        => Assert.Equal(expected, CriticalStrikePolicy.OnLowLife(life, maximum));

    [Theory]
    [InlineData(3000, 0, false, 3000, 0)]
    [InlineData(3000, 1, false, 5100, 0)]
    [InlineData(3000, -1, false, 900, 0)]
    [InlineData(3000, 0, true, 5100, 900)]
    [InlineData(3000, 1, true, 7599, 2601)]
    [InlineData(0, 1, true, 0, 0)]
    [InlineData(10000, -1, true, 10000, 10000)]
    [InlineData(12000, 0, true, 10000, 10000)]
    public void Effective_spell_critical_chance_applies_luck_then_bifurcation(int chance, int extra, bool bifurcate,
        int effective, int twice)
    {
        Assert.Equal(effective, CriticalStrikePolicy.EffectiveChance(chance, extra, bifurcate));
        Assert.Equal(twice, CriticalStrikePolicy.DoubleCriticalChance(chance, extra, bifurcate));
    }
}

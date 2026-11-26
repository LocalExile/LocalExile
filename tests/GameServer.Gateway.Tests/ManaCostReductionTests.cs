namespace GameServer.Gateway.Tests;

public sealed class ManaCostReductionTests
{
    static Func<uint, int> Stats(int reduction, int flat = 0, int increase = 0) => stat =>
        stat == Stat.BaseManaCostMinusPct ? reduction
        : stat == Stat.SkillManaCostPlus ? flat
        : stat == Stat.ManaCostPlusPct ? increase
        : 0;

    // The published cost takes a floored reduction off the base: base 55 with a flat -14 publishes 16 at 47% less
    // (flooring the product would give 15) and 14 at 50% less (13).
    [Fact]
    public void Reduced_mana_cost_floors_the_reduction_amount()
    {
        Assert.Equal(16, ManaRecoveryRules.Cost(55, Stats(47, flat: -14)));
        Assert.Equal(14, ManaRecoveryRules.Cost(55, Stats(50, flat: -14)));
        Assert.Equal(10, ManaRecoveryRules.Cost(13, Stats(30)));
    }

    [Fact]
    public void Mana_cost_without_a_fractional_reduction_is_unchanged()
    {
        Assert.Equal(30, ManaRecoveryRules.Cost(60, Stats(50)));
        Assert.Equal(60, ManaRecoveryRules.Cost(60, Stats(0)));
        Assert.Equal(0, ManaRecoveryRules.Cost(60, Stats(100)));
    }
}

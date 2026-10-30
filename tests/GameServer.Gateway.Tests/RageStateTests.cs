namespace GameServer.Gateway.Tests;

public class RageStateTests
{
    [Theory]
    [InlineData(-100, 30)]
    public void Loss_speed_changes_tenths_per_second_after_the_same_two_second_delay(int percent, int expected)
    {
        var input = new RageState.Inputs(LossRatePercent: percent);
        var rage = new RageState(); rage.Gain(30, 0, input);
        Assert.Equal(2, input.LossDelaySeconds);
        Assert.Equal(30, rage.Read(2, input).Current);
        Assert.Equal(expected, rage.Read(3, input).Current);
    }

    static RageState.Inputs Sheer => new(MaximumBonus: 15, MaximumOnHitGainChance: 15,
        GainOnHit: 1, GainOnAttackHit: 2, GainOnMeleeHit: 3, GainOnAttackCritical: 4, GainOnMeleeCritical: 5,
        GainWhenHit: 6);

}

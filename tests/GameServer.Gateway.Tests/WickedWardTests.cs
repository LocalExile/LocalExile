namespace GameServer.Gateway.Tests;

public sealed class WickedWardTests
{
    [Fact]
    public void Wicked_ward_protects_only_an_active_recharge_started_within_the_last_four_seconds()
    {
        var shield = new EnergyShield();
        int Stats(string name) => name == EnergyShield.WickedWard ? 1 : 0;
        shield.SetMaximum(300, refill: true);
        shield.Damage(300, bypass: false, now: 0);
        shield.Damage(1, bypass: false, now: 1);
        shield.Advance(1, 2.5, Stats, alive: true);
        Assert.False(shield.Recharging);
        Assert.Equal(0u, shield.Current);

        shield.Advance(2.5, 3.5, Stats, alive: true);
        Assert.True(shield.Recharging);
        Assert.Equal(30u, shield.Current);
        shield.Damage(20, bypass: false, now: 3.5);
        Assert.True(shield.Recharging);
        shield.Advance(3.5, 7, Stats, alive: true);
        Assert.Equal(220u, shield.Current);

        shield.Damage(50, bypass: false, now: 7);
        Assert.False(shield.Recharging);
        shield.Advance(7, 7.5, Stats, alive: true);
        Assert.Equal(170u, shield.Current);
        Assert.False(shield.RechargeReady(7.5, Stats));
    }
}

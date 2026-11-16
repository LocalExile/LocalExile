namespace GameServer.Gateway.Tests;

public sealed class EnergyShieldNotDelayedTests
{
    static Func<string, int> Stats(bool notDelayed) =>
        name => notDelayed && name == "energy_shield_recharge_not_delayed_by_damage" ? 1 : 0;

    // Vaal Discipline: while the stat holds, damage taken does not hold back recharge (base 2 s delay otherwise).
    [Fact]
    public void DamageDoesNotDelayRechargeWhileTheStatHolds()
    {
        var plain = new EnergyShield();
        var undelayed = new EnergyShield();
        plain.SetMaximum(300, refill: true);
        undelayed.SetMaximum(300, refill: true);
        plain.Damage(150, false, 10);
        undelayed.Damage(150, false, 10);

        plain.Advance(10, 11, Stats(false), true);
        undelayed.Advance(10, 11, Stats(true), true);

        // 300 maximum recharges 2000% a minute: 100 a second.
        Assert.Equal(150u, plain.Current);
        Assert.Equal(250u, undelayed.Current);
        Assert.True(undelayed.Recharging);
        Assert.False(plain.RechargeReady(11, Stats(false)));
        Assert.True(undelayed.RechargeReady(11, Stats(true)));
    }
}

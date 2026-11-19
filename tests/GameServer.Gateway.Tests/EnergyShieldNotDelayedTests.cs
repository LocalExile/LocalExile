namespace GameServer.Gateway.Tests;

public sealed class EnergyShieldNotDelayedTests
{
    static Func<uint, int> Stats(bool notDelayed) =>
        name => notDelayed && name == Stat.EnergyShieldRechargeNotDelayedByDamage ? 1 : 0;

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
    [Fact]
    public void VaalAuraExpiryUsesGameTimeAndRemovesBothBuffsOnce()
    {
        var conn = new Player(new(1, "vaal", 100, 100, 10, 10, null, []), 1, 1) { PlayerLife = 100 };
        var aura = new Game.VaalAuraState { Buff = 11, Source = 12, Until = 3, Stat = global::Stat.EnergyShieldRechargeNotDelayedByDamage };
        Game.VaalAuraStates.Add(conn, aura);
        try
        {
            Assert.Null(Game.ExpireVaalAura(conn, 2));
            conn.Clock.Stop();
            Assert.Null(Game.ExpireVaalAura(conn, 3)!);
            Assert.Equal(11, aura.Buff);
            conn.Clock.Start();
            Assert.Null(Game.ExpireVaalAura(conn, 2));
            Assert.Equal([.. GameServer.Core.Protocol.Instance.InstanceEntities.RemoveBuff(Program.PlayerId, 11),
                .. GameServer.Core.Protocol.Instance.InstanceEntities.RemoveBuff(Program.PlayerId, 12)], Game.ExpireVaalAura(conn, 3)!);
            Assert.Null(aura.Stat);
            Assert.Null(Game.ExpireVaalAura(conn, 4));
        }
        finally { Game.VaalAuraStates.Remove(conn); }
    }
}

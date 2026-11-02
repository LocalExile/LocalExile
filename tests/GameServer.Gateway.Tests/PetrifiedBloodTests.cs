using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class PetrifiedBloodTests
{
    static ClientState Player(uint life, uint maximumLife)
    {
        var conn = new ClientState(new(1, "petrified", 300, 300, 100, 100, null, []), 1, 1)
        {
            Pools = new(1, maximumLife, 1000, 0, 0),
            PlayerLife = life,
        };
        return conn;
    }

    // A hit is split at half Life: the part above it is taken at once, and only the part below it is protected.
    // At level 20 the gem loses 81% of the 40% it prevents over four seconds, so 19% of that part is permanent
    // mitigation. Level 1 loses all 40%, which is pure deferral.
    [Fact]
    public void Hits_below_half_life_are_partly_deferred_over_four_seconds()
    {
        var conn = Player(600, 1000);
        conn.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 7, now: 0);
        var noStats = new Dictionary<uint, int>();
        // 100 of the 300 is above half Life; of the 200 below it 80 is prevented, and 81% of 80 is deferred.
        Assert.Equal(220, Game.DamagePlayerHit(conn, 300, 0, 0, noStats));
        conn.PlayerLife = 380;
        Assert.Equal(32.0, conn.PetrifiedBlood.Take(2), 6);   // 16.2 per second, whole points
        Assert.Equal(32.0, conn.PetrifiedBlood.Take(4), 6);
        Assert.Equal(0.0, conn.PetrifiedBlood.Take(6), 6);
        Assert.Equal(350, PetrifiedBlood.RequiredMana(conn.Pools, 3500));

        // A hit wholly below half Life protects 40% of all of it; level 1 defers every protected point.
        var low = Player(400, 1000);
        low.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 100, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 8, now: 0);
        Assert.Equal(60, Game.DamagePlayerHit(low, 100, 0, 0, noStats));
        low.PlayerLife = 340;
        Assert.Equal(40.0, low.PetrifiedBlood.Take(4), 6);

        // The shared default Low Life boundary is inclusive 50%: 34 of 66 is above, 33 is low.
        var tiny = Player(34, 66);
        tiny.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 100, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 9, now: 0);
        Assert.False(PetrifiedBlood.OnLowLife(tiny));
        tiny.PlayerLife = 33;
        Assert.True(PetrifiedBlood.OnLowLife(tiny));
    }

    // Recovery other than flasks stops at the Low Life boundary while the buff runs; a flask still fills the pool.
    // The skill life cost applies only above Low Life and disappears when the buff or the condition does. A threshold
    // stat can move the boundary (40% here) and the buff follows it.
    [Fact]
    public void Recovery_stops_at_low_life_and_skills_cost_life_above_it()
    {
        var conn = Player(300, 1000);
        conn.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 40, buff: 7, now: 0);
        double carry = 0;
        Assert.Equal(400u, PetrifiedBlood.RecoverLife(conn, 300, 500, ref carry));
        double flaskCarry = 0;
        Assert.Equal(1000u, RecoveryPool.Advance(400, 1000, 600, ref flaskCarry));

        conn.PlayerLife = 401;
        Assert.False(PetrifiedBlood.OnLowLife(conn));
        Assert.True(PetrifiedBlood.BlocksRecovery(conn));   // at/above the boundary: no non-flask gain
        Assert.Equal(4, PetrifiedBlood.LifeCost(conn, 10));
        conn.PlayerLife = 400;
        Assert.True(PetrifiedBlood.OnLowLife(conn));
        Assert.True(PetrifiedBlood.BlocksRecovery(conn));
        Assert.Equal(0, PetrifiedBlood.LifeCost(conn, 10));
        conn.PlayerLife = 399;
        Assert.True(PetrifiedBlood.OnLowLife(conn));
        Assert.False(PetrifiedBlood.BlocksRecovery(conn));  // below the boundary: regen may reach it
        Assert.Equal(0, PetrifiedBlood.LifeCost(conn, 10));
        conn.PlayerLife = 600;
        conn.PetrifiedBlood.RefreshThreshold(50);
        Assert.False(PetrifiedBlood.OnLowLife(conn));
        Assert.True(PetrifiedBlood.BlocksRecovery(conn));
        Assert.Equal(4, PetrifiedBlood.LifeCost(conn, 10));
        conn.PlayerLife = 500;
        Assert.True(PetrifiedBlood.OnLowLife(conn));
        Assert.True(PetrifiedBlood.BlocksRecovery(conn));
        Assert.Equal(0, PetrifiedBlood.LifeCost(conn, 10));
        conn.PlayerLife = 499;
        Assert.True(PetrifiedBlood.OnLowLife(conn));
        Assert.False(PetrifiedBlood.BlocksRecovery(conn));
        conn.PetrifiedBlood.Suspend();
        conn.PlayerLife = 600;
        Assert.Equal(0, PetrifiedBlood.LifeCost(conn, 10));
    }
}

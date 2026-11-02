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
        var loss = conn.PetrifiedBlood.Display(0);
        Assert.Equal(972, loss.PerMinute);   // 64.8 deferred over 4 s = 16.2/s
        Assert.Equal(4, loss.Expiry, 6);
        conn.PlayerLife = 380;
        Assert.Equal(32.0, conn.PetrifiedBlood.Take(2), 6);   // 16.2 per second, whole points
        Assert.Equal(32.0, conn.PetrifiedBlood.Take(4), 6);
        Assert.Equal(0.0, conn.PetrifiedBlood.Take(6), 6);
        Assert.Equal(0, conn.PetrifiedBlood.Display(6).PerMinute);
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
    // Life leech instances keep their full-Life cap and are not removed at Low Life, but their actual recovery is
    // capped like regeneration and Recoup. The skill life cost applies only above Low Life and disappears when the
    // buff or the condition does. A threshold stat can move the boundary (40% here) and the buff follows it.
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

        // Instant leech above Low Life: the hit grants 50, but the actual recovery stops at the current pool. The
        // same hit without Petrified Blood reaches 850, proving the 50-point grant.
        static int InstantOnly(string name) => name == "leech_%_is_instant" ? 100 : 0;
        var plain = Player(800, 1000);
        Game.LeechOf(plain).Configure(0,
            LeechLimits.Resolve(InstantOnly, plain.Pools.Life, plain.Pools.Mana, plain.Pools.EnergyShield),
            InstantOnly, _ => null);
        var leechSources = new LeechSources(DamagePacket.Of(DamageType.Physical, 1000, 1000),
            DamagePacket.Empty, DamagePacket.Empty) { Attack = true };
        var leechTarget = new SpawnedMonster("leech", new(0, 0, 0), 0, 0, 1000) { Life = 1000 };
        Game.LeechOnHitLocked(plain, leechTarget, DamagePacket.Of(DamageType.Physical, 500, 500), 500, leechSources);
        Assert.Equal(850u, plain.PlayerLife);

        var instant = Player(800, 1000);
        instant.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 8, now: 0);
        Game.LeechOf(instant).Configure(0,
            LeechLimits.Resolve(InstantOnly, instant.Pools.Life, instant.Pools.Mana, instant.Pools.EnergyShield),
            InstantOnly, _ => null);
        Game.LeechOnHitLocked(instant, leechTarget, DamagePacket.Of(DamageType.Physical, 500, 500), 500, leechSources);
        Assert.Equal(800u, instant.PlayerLife);

        // A timed instance is retained at Low Life (its own cap is full Life), but its 50-point recovery is still
        // ignored above the boundary by the same rule as regeneration and Recoup.
        static int NoInstant(string _) => 0;
        var retained = Player(800, 1000);
        retained.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 9, now: 0);
        Game.LeechOf(retained).Configure(0,
            LeechLimits.Resolve(NoInstant, retained.Pools.Life, retained.Pools.Mana, retained.Pools.EnergyShield),
            NoInstant, _ => null);
        Game.LeechOnHitLocked(retained, new("leech retained", new(0, 0, 0), 0, 0, 1000) { Life = 1000 },
            DamagePacket.Of(DamageType.Physical, 500, 500), 500, leechSources);
        Assert.Equal(800u, retained.PlayerLife);
        Assert.Equal(1, Game.LeechOf(retained).Life.Count);
        double leechCarry = 0;
        Assert.Equal(800u, PetrifiedBlood.RecoverLife(retained, 800, 50, ref leechCarry));
        // The orb display keeps the full leech rate while PB caps the actual pool update.
        var display = Game.LeechOf(retained).Display(0);
        Assert.Equal(1200, display.Life.PerMinute);
        Assert.True(display.Life.Expiry > 0);

        // Recoup uses the pre-PB hit and keeps its own four-second rate at Low Life; only the actual recovery is
        // capped. 30% of a 1000 hit is 300, paid as 75 per second, and 450 + 75 stops at the 500 Low Life boundary.
        var recoupPlayer = Player(400, 1000);
        recoupPlayer.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 11, now: 0);
        var recoupStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("damage_taken_goes_to_life_over_4_seconds_%")!.Value] = 30,
        };
        Game.RecoupHitLocked(recoupPlayer, DamagePacket.Of(DamageType.Physical, 1000, 1000), 0, recoupStats);
        Assert.Equal(75, recoupPlayer.LifeRecoup.Take(1), 6);
        double recoupCarry = 0;
        Assert.Equal(500u, PetrifiedBlood.RecoverLife(recoupPlayer, 450, 75, ref recoupCarry));
        Assert.Equal(500u, PetrifiedBlood.RecoverLife(recoupPlayer, 500, 75, ref recoupCarry));

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

namespace GameServer.Gateway.Tests;

public sealed class MonsterContagionTests
{
    [Fact]
    public void Contagion_ticks_does_not_stack_and_expires()
    {
        var contagion = new MonsterContagion();
        Assert.True(contagion.Idle);

        contagion.Apply(0, new(100, 4, 20));
        // Half the monster's chaos damage gets through (factor 0.5): 100/s x 0.5 x 1 s.
        Assert.Equal(50, contagion.Advance(1, 0.5));

        // A stronger, shorter cast deals its damage until it ends (t = 2); the first resumes after it, still ending at 4.
        contagion.Apply(1, new(300, 1, 20));
        Assert.Equal(300, contagion.Advance(2, 1));
        Assert.Equal(100, contagion.Advance(3, 1));
        Assert.Equal(4, contagion.Until);

        // A weaker cast never lengthens the stronger one: 100/s to t = 4, then the weaker 40/s alone to its own end (t = 6).
        contagion.Apply(3, new(40, 3, 20));
        Assert.Equal(100, contagion.Advance(4, 1));
        Assert.Equal(40, contagion.Active!.DamagePerSecond);
        Assert.Equal(80, contagion.Advance(6, 1));
        Assert.True(contagion.Idle);
        Assert.Equal(0, contagion.Advance(7, 1));

        // A weaker recast after the old cast ended (no Advance between): the old one's owed second is paid, then only the
        // recast deals damage, until its own end.
        var recast = new MonsterContagion();
        recast.Apply(0, new(100, 1, 18));
        recast.Apply(1.01, new(1, 5, 18));
        Assert.Equal(100 + 1, recast.Advance(2.01, 1));
        Assert.Equal(1, recast.Active!.DamagePerSecond);
        Assert.Equal(6.01, recast.Until, 6);
    }

    [Fact]
    public void Contagion_spreads_the_time_it_had_left_once()
    {
        // Killed by a hit at t = 1 of a 4 s cast: 3 s left spread.
        var hit = new MonsterContagion();
        hit.Apply(0, new(100, 4, 18));
        Assert.Equal(new MonsterContagion.Cast(100, 3, 18), hit.TakeSpread(1));
        Assert.Null(hit.TakeSpread(1));

        // A tick clears a monster a hit killed before the spread runs: what it had left at that tick still spreads.
        var cleared = new MonsterContagion();
        cleared.Apply(0, new(100, 4, 18));
        cleared.Advance(1, 1);
        cleared.Clear();
        Assert.Equal(3.0, cleared.TakeSpread(1.5)!.Seconds, 6);

        // Killed by its own tick a quarter of the way through: the 2 s cast that ended in that tick still had 1.5 s left.
        var lethal = new MonsterContagion();
        lethal.Apply(0, new(100, 2, 18));
        Assert.Equal(200, lethal.Advance(2, 1));
        lethal.Died(0.25);
        Assert.Equal(1.5, lethal.TakeSpread(2)!.Seconds, 6);

        // Killed exactly as it ended (the whole final tick's damage), or dying after a nonlethal end: nothing left to spread.
        var atEnd = new MonsterContagion();
        atEnd.Apply(0, new(100, 1, 18));
        Assert.Equal(100, atEnd.Advance(1, 1));
        atEnd.Died(1);
        Assert.Null(atEnd.TakeSpread(1));
        var expired = new MonsterContagion();
        expired.Apply(0, new(100, 1, 18));
        expired.Advance(1, 1);
        expired.Clear();
        Assert.Null(expired.TakeSpread(3));

        // A spread chain ends: each spread carries less time, so a dense pack runs out.
        var chain = new MonsterContagion();
        chain.Apply(0, new(100, 4, 18, SpreadMore: 0.75));
        var next = new MonsterContagion();
        next.Apply(3, chain.TakeSpread(3)! with { DamagePerSecond = 75 });
        Assert.Equal(4, next.Until);
        Assert.Equal(new MonsterContagion.Cast(75, 0.5, 18, 0.75), next.TakeSpread(3.5));
    }
}

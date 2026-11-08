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

        // A weaker cast does not replace it, but its later expiry keeps the debuff going.
        contagion.Apply(1, new(40, 4, 20));
        Assert.Equal(100, contagion.Active!.DamagePerSecond);
        Assert.Equal(5, contagion.Until);

        // A stronger one does: 300/s from here.
        contagion.Apply(2, new(300, 1, 20));
        Assert.Equal(100 + 300, contagion.Advance(3, 1));

        // It ends at t = 5 and deals nothing after.
        Assert.Equal(600, contagion.Advance(6, 1));
        Assert.Null(contagion.Active);
        Assert.Equal(0, contagion.Advance(7, 1));
    }

    [Fact]
    public void Contagion_spreads_once_from_a_monster_that_died_affected()
    {
        var unaffected = new MonsterContagion();
        Assert.Null(unaffected.TakeSpread());

        var killedByHit = new MonsterContagion();
        killedByHit.Apply(0, new(100, 5, 18, SpreadMore: 0.75));
        var cast = killedByHit.TakeSpread();
        Assert.Equal(new MonsterContagion.Cast(100, 5, 18, 0.75), cast);
        Assert.Null(killedByHit.TakeSpread());

        // Killed by its own damage over time: the tick clears it first, and the spread still finds the cast.
        var killedByDot = new MonsterContagion();
        killedByDot.Apply(0, new(100, 5, 18));
        killedByDot.Clear();
        Assert.Equal(18, killedByDot.TakeSpread()?.Radius);
        Assert.Null(killedByDot.TakeSpread());
    }
}

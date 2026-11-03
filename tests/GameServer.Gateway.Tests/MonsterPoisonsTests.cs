namespace GameServer.Gateway.Tests;

public sealed class MonsterPoisonsTests
{
    [Fact]
    public void Poisons_stack_and_deal_concurrent_damage()
    {
        var poisons = new MonsterPoisons();
        Assert.True(poisons.Idle);
        Assert.False(poisons.Poisoned);

        // Add two poison stacks of 100 DPS lasting 2 seconds at t=0
        Assert.True(poisons.Add(0, 100, 2));
        Assert.True(poisons.Add(0, 100, 2));

        Assert.True(poisons.Poisoned);
        Assert.Equal(2, poisons.Stacks(0.5));
        Assert.Equal(200, poisons.TotalDps(0.5));

        // Advance 1 second with 100% factor (0% resistance):
        // 200 DPS * 1s = 200 damage
        int damage = poisons.Advance(1.0, 1.0);
        Assert.Equal(200, damage);
        Assert.Equal(2, poisons.Stacks(1.0));

        // Add a third stack at t=1.0 lasting 2s (expires at t=3.0)
        Assert.True(poisons.Add(1.0, 150, 2));
        Assert.Equal(3, poisons.Stacks(1.5));
        Assert.Equal(350, poisons.TotalDps(1.5));

        // Advance to t=2.0 (stacks 1 and 2 expire at 2.0):
        // 1s elapsed at 350 DPS = 350 damage
        damage = poisons.Advance(2.0, 1.0);
        Assert.Equal(350, damage);

        // Advance to t=3.0: only stack 3 was active from 2.0 to 3.0 (150 DPS * 1s = 150 damage)
        damage = poisons.Advance(3.0, 1.0);
        Assert.Equal(150, damage);

        // Stacks have all expired at t=3.0
        Assert.Equal(0, poisons.Stacks(3.5));
        Assert.False(poisons.Poisoned);
    }

    [Fact]
    public void Chaos_resistance_mitigates_poison_damage()
    {
        var poisons = new MonsterPoisons();
        poisons.Add(0, 100, 2);

        // Factor 0.5 (e.g. 50% chaos resistance):
        // 100 DPS * 1s * 0.5 = 50 damage
        int damage = poisons.Advance(1.0, 0.5);
        Assert.Equal(50, damage);
    }
}

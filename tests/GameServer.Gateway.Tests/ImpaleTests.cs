namespace GameServer.Gateway.Tests;

public class ImpaleTests
{
    [Fact]
    public void An_impale_stores_a_tenth_of_the_hits_physical_damage()
    {
        var impale = new ImpaleState();
        Assert.True(impale.Apply(120));
        Assert.Equal(1, impale.Stacks);
        Assert.Equal(12, impale.Bonus, 6);
    }

    [Fact]
    public void Stacks_are_limited_by_hit_consumption_and_expiry_not_an_artificial_cap()
    {
        var impale = new ImpaleState();
        for (int i = 0; i < 5; i++) Assert.True(impale.Apply(100));
        Assert.Equal(5, impale.Stacks);
        Assert.Equal(50, impale.Bonus, 6);
        Assert.True(impale.Apply(100));
        Assert.Equal(6, impale.Stacks);
        Assert.Equal(60, impale.Bonus);
    }

    [Fact]
    public void Instances_expire_independently_at_the_eight_second_boundary()
    {
        var impale = new ImpaleState();
        impale.Apply(100, now: 1);
        impale.Apply(200, now: 2);
        impale.Expire(8.999);
        Assert.Equal(30, impale.Bonus);
        impale.Expire(9);
        Assert.Equal(20, impale.Bonus);
        impale.Expire(10);
        Assert.Equal(0, impale.Stacks);
    }

    [Fact]
    public void Any_later_hit_triggers_existing_instances_before_the_new_instance_is_added()
    {
        var impale = new ImpaleState();
        var first = impale.OnHit(200, 1, new(100, 50, 2, 4), new Random(1));
        Assert.Equal((0d, true), first);
        Assert.Equal(30, impale.Bonus);
        Assert.Equal((30d, false), impale.OnHit(0, 2, new(0), new Random(1)));
        Assert.Equal((30d, true), impale.OnHit(100, 3, new(100), new Random(1)));
        Assert.Equal(10, impale.Bonus);
        Assert.Equal((0d, false), impale.OnHit(0, 11, new(0), new Random(1)));
    }

    [Theory]
    [InlineData(-100, 5, 8)]
    [InlineData(0, 0, 8)]
    [InlineData(0, 5, 0)]
    public void Zero_effect_hits_or_duration_cannot_create_an_instance(int effect, int hits, double duration)
    {
        var impale = new ImpaleState();
        Assert.False(impale.Apply(100, effectPercent: effect, hits: hits, duration: duration));
        Assert.Equal(0, impale.Stacks);
    }

}

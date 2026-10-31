namespace GameServer.Gateway.Tests;

public class LeechRecoveryStateTests
{
    [Fact]
    public void Ggg_worked_example_keeps_duration_separate_from_total_recovery_and_cap()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(2, 230, 1)); // 100% total recovery, 15% increased maximum total recovery.
        Assert.Equal(0, state.Grant(0, 700 * .3, 1000, 150)); // 50% increased maximum recovery per instance.
        Assert.Equal(40, state.TakeRecovery(1));
        Assert.Equal(1, state.Count);
        Assert.Equal(260, state.TakeRecovery(7.5)); // 300 actual recovered over 7.5s from a nominal 150 instance.
        Assert.Equal(0, state.Count);
        Assert.Equal(0, state.TakeRecovery(20));
    }

    [Fact]
    public void Ggg_six_instance_example_caps_the_sum_then_applies_generic_recovery()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(2, 230, 1.5));
        for (int i = 0; i < 6; i++) state.Grant(0, 210, 1000, 150);
        Assert.Equal(345, state.TakeRecovery(1)); // min(6 * 20 * 2, 230) * 1.5
        Assert.Equal(6, state.Count);
        state.TakeRecovery(7.5);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Delayed_updates_integrate_every_expiry_and_do_not_bank_capped_instances()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(1, 30, 1));
        state.Grant(0, 20, 1000, 100); // 20/s for one second.
        state.Grant(0, 100, 1000, 100); // Another 20/s for five seconds.
        Assert.Equal(110, state.TakeRecovery(10)); // 30 for first second, then 20 for four seconds.
        Assert.Equal(0, state.Count);
        Assert.Equal(0, state.TakeRecovery(11));
    }

    [Fact]
    public void Configuration_changes_settle_old_rates_before_adopting_new_ones()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(1, 200, 1));
        state.Grant(0, 100, 1000, 100);
        state.Configure(2, new(2, 200, 1));
        Assert.Equal(80, state.TakeRecovery(3)); // 2 * 20 + 1 * 40
        state.Configure(4, new(2, 0, 1));
        Assert.Equal(40, state.TakeRecovery(10));
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Instant_portions_reduce_timed_duration_without_receiving_recovery_rate_multipliers()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(2, 200, 1.5));
        Assert.Equal(25, state.Grant(0, 100, 1000, 100, 25));
        Assert.Equal(225, state.TakeRecovery(3.75)); // remaining 75 / 20 duration, 20 * 2 * 1.5 rate
        Assert.Equal(0, state.Count);
        Assert.Equal(100, state.Grant(4, 1000, 1000, 100, 200));
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Clearing_discards_pending_recovery_and_instances_and_repeated_game_time_adds_nothing()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(1, 200, 1));
        state.Grant(0, 100, 1000, 100);
        Assert.Equal(20, state.TakeRecovery(1));
        Assert.Equal(0, state.TakeRecovery(1));
        Assert.Equal(0, state.TakeRecovery(.5));
        state.Configure(2, new(1, 200, 1));
        state.Clear(2);
        Assert.Equal(0, state.Count);
        Assert.Equal(0, state.TakeRecovery(10));
    }

    [Theory]
    [InlineData(0u, 100, 10)]
    [InlineData(100u, 0, 10)]
    [InlineData(100u, 100, 0)]
    [InlineData(100u, -100, 10)]
    public void Empty_pools_or_nonpositive_amounts_cannot_create_instances(uint pool, double amount, double cap)
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(1, 200, 1));
        Assert.Equal(0, state.Grant(0, amount, pool, cap));
        Assert.Equal(0, state.Count);
        Assert.Equal(0, state.TakeRecovery(100));
    }
}

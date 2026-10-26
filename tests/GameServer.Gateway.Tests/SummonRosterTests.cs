namespace GameServer.Gateway.Tests;

public sealed class SummonRosterTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T00:00:00Z");

    [Fact]
    public void Cooldown_and_limit_refresh_an_existing_wolf_with_full_life()
    {
        var roster = new SummonRoster();
        var owner = Guid.NewGuid();
        roster.Attach(owner);
        var first = roster.Trigger(owner, Now, false, 2, 30000, 500)!;
        Assert.Null(roster.Trigger(owner, Now.AddMilliseconds(499), false, 2, 30000, 500));
        var second = roster.Trigger(owner, Now.AddMilliseconds(500), false, 2, 30000, 500)!;
        roster.SetLife(owner, first.Id, 1);
        var refreshed = roster.Trigger(owner, Now.AddSeconds(1), false, 2, 30000, 500)!;
        Assert.Same(first, refreshed);
        Assert.Null(refreshed.Life);
        Assert.True(refreshed.RefreshLife);
        Assert.Equal(Now.AddSeconds(31), refreshed.Expires);
        Assert.Equal(new[] { first, second }, roster.Read(owner, Now.AddSeconds(1), false, true, 2));
    }

    [Fact]
    public void Transfer_keeps_life_and_deadline_and_rejects_the_old_connection()
    {
        var roster = new SummonRoster();
        var old = Guid.NewGuid(); var next = Guid.NewGuid();
        roster.Attach(old);
        var wolf = roster.Trigger(old, Now, false, 10, 30000, 500)!;
        roster.SetLife(old, wolf.Id, 123);
        roster.Attach(next);
        roster.Release(old, false); // an old socket finally closes after the new one is already active
        Assert.False(roster.SetLife(old, wolf.Id, 0));
        Assert.Null(roster.Trigger(old, Now.AddSeconds(2), false, 10, 30000, 500));
        var transferred = Assert.Single(roster.Read(next, Now.AddSeconds(2), false, true, 10));
        Assert.Equal((uint)123, transferred.Life);
        Assert.Equal(Now.AddSeconds(30), transferred.Expires);
        Assert.Empty(roster.Read(next, Now.AddSeconds(30), false, true, 10));
    }

    [Fact]
    public void Pause_freezes_expiry_and_cooldown_but_does_not_permit_a_trigger()
    {
        var roster = new SummonRoster(); var owner = Guid.NewGuid(); roster.Attach(owner);
        var wolf = roster.Trigger(owner, Now, false, 10, 30000, 500)!;
        Assert.Single(roster.Read(owner, Now, true, true, 10));
        Assert.Single(roster.Read(owner, Now.AddMinutes(1), true, true, 10));
        Assert.Null(roster.Trigger(owner, Now.AddMinutes(1), true, 10, 30000, 500));
        Assert.Single(roster.Read(owner, Now.AddMinutes(1), false, true, 10));
        Assert.Equal(Now.AddSeconds(90), wolf.Expires);
        Assert.Null(roster.Trigger(owner, Now.AddSeconds(60.499), false, 10, 30000, 500));
        Assert.NotNull(roster.Trigger(owner, Now.AddSeconds(60.5), false, 10, 30000, 500));
    }

    [Fact]
    public void Refund_death_lower_limit_and_logout_remove_owned_summons()
    {
        var roster = new SummonRoster(); var owner = Guid.NewGuid(); roster.Attach(owner);
        void Add() => Assert.NotNull(roster.Trigger(owner, Now.AddSeconds(10), false, 10, 30000, 0));
        Add(); Add();
        Assert.Single(roster.Read(owner, Now.AddSeconds(10), false, true, 1));
        Assert.Empty(roster.Read(owner, Now.AddSeconds(10), false, false, 10));
        Add(); var wolf = Assert.Single(roster.Read(owner, Now.AddSeconds(10), false, true, 10));
        roster.SetLife(owner, wolf.Id, 0);
        Assert.Empty(roster.Read(owner, Now.AddSeconds(10), false, true, 10));
        Add(); roster.Clear(owner);
        Assert.Empty(roster.Read(owner, Now.AddSeconds(10), false, true, 10));
        Add(); roster.Release(owner, false); roster.Attach(owner);
        Assert.Empty(roster.Read(owner, Now.AddSeconds(10), false, true, 10));
    }

    [Fact]
    public void Pause_boundaries_between_refresh_ticks_preserve_exact_active_lifetime()
    {
        var roster = new SummonRoster(); var owner = Guid.NewGuid(); roster.Attach(owner);
        var wolf = roster.Trigger(owner, Now, false, 10, 30000, 500)!;
        roster.Pause(owner, Now.AddMilliseconds(100), true);
        roster.Pause(owner, Now.AddMilliseconds(200), false); // no intervening Read
        Assert.Equal(Now.AddMilliseconds(30100), wolf.Expires);
        Assert.Single(roster.Read(owner, Now.AddMilliseconds(30050), false, true, 10));
        Assert.Empty(roster.Read(owner, Now.AddMilliseconds(30100), false, true, 10));
    }

    [Fact]
    public void Old_lease_cannot_consume_a_refresh_requested_by_the_new_area()
    {
        var roster = new SummonRoster(); var old = Guid.NewGuid(); var next = Guid.NewGuid(); roster.Attach(old);
        var wolf = roster.Trigger(old, Now, false, 1, 30000, 500)!;
        roster.Attach(next);
        roster.Trigger(next, Now.AddSeconds(1), false, 1, 30000, 500);
        Assert.False(roster.TakeRefresh(old, wolf.Id));
        Assert.True(roster.TakeRefresh(next, wolf.Id));
        Assert.False(roster.TakeRefresh(next, wolf.Id));
    }

    [Fact]
    public void Concurrent_buff_allocations_never_reuse_an_instance()
    {
        var state = new ClientState(new(0, "BuffAllocation", 128, 128, 32, 32, null, []), 0, 1);
        var allocated = new ushort[8192];
        Parallel.For(0, allocated.Length, i => allocated[i] = state.AllocateBuff());
        Assert.Equal(allocated.Length, allocated.Distinct().Count());
        Assert.DoesNotContain((ushort)0, allocated);
        state.NextBuff = ushort.MaxValue;
        Assert.Equal(ushort.MaxValue, state.AllocateBuff());
        Assert.Throws<InvalidOperationException>(() => state.AllocateBuff());
    }
}

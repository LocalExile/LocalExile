using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class HarbingerRosterTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    static HarbingerCompanionCatalog.Companion Source(bool channel = false)
    {
        var buff = new HarbingerCompanionCatalog.BuffSkill(7, "buff", 1, 700, 4000, 8000, true, 895,
            "test", 0, new Dictionary<uint, int>(), new Dictionary<uint, int> { [100] = 20 });
        return new(20105, HarbingerCompanionCatalog.Kind.Arcane, 1, 1, 12, 2, 1,
            new(332, "minion", "minion", "minion", null, MonsterObject.BaseComponents), 3, channel,
            new Dictionary<uint, int>(), [buff]) { CountStat = 4 };
    }

    [Fact]
    public void Initial_cooldown_cast_and_buff_have_distinct_deadlines()
    {
        var roster = new HarbingerRoster(); var lease = Guid.NewGuid(); roster.Attach(lease, Now);
        Assert.True(roster.Activate(lease, Source(), 100, Now));
        Assert.Empty(Assert.Single(roster.Step(lease, Now.AddMilliseconds(3900), true, false)).Change.Started);
        Assert.Single(Assert.Single(roster.Step(lease, Now.AddSeconds(4), true, false)).Change.Started);
        Assert.Empty(roster.Buffs(lease, Now.AddMilliseconds(4600)));
        Assert.Single(Assert.Single(roster.Step(lease, Now.AddMilliseconds(4700), true, false)).Change.Completed);
        Assert.Equal(8.7, Assert.Single(roster.Buffs(lease, Now.AddSeconds(5))).Buff.EndsAt, 7);
        Assert.Empty(roster.Buffs(lease, Now.AddMilliseconds(8700)));
    }

    [Fact]
    public void Short_pause_between_ticks_is_exact_and_does_not_use_wall_time()
    {
        var roster = new HarbingerRoster(); var lease = Guid.NewGuid(); roster.Attach(lease, Now);
        roster.Activate(lease, Source(), 100, Now);
        roster.Pause(lease, Now.AddSeconds(1), true);
        roster.Pause(lease, Now.AddSeconds(11), false);
        Assert.Equal(1, roster.Time(lease, Now.AddSeconds(11)));
        Assert.Empty(Assert.Single(roster.Step(lease, Now.AddMilliseconds(13900), true, false)).Change.Started);
        Assert.Single(Assert.Single(roster.Step(lease, Now.AddSeconds(14), true, false)).Change.Started);
    }

    [Fact]
    public void Transfer_keeps_original_buff_deadline_and_revokes_the_old_lease()
    {
        var roster = new HarbingerRoster(); var old = Guid.NewGuid(); var next = Guid.NewGuid(); roster.Attach(old, Now);
        roster.Activate(old, Source(), 100, Now);
        roster.Step(old, Now.AddSeconds(4), true, false);
        roster.Release(old, true, Now.AddMilliseconds(4100));
        roster.Attach(next, Now.AddSeconds(6));
        Assert.Empty(roster.Step(old, Now.AddSeconds(7), true, false));
        roster.Clear(old); roster.Release(old, false, Now.AddSeconds(7));
        roster.Step(next, Now.AddSeconds(6), true, false);
        Assert.Equal(8.7, Assert.Single(roster.Buffs(next, Now.AddSeconds(6))).Buff.EndsAt, 7);
        Assert.Empty(roster.Buffs(next, Now.AddSeconds(9)));
    }

    [Fact]
    public void Source_removal_logout_and_death_clear_pending_casts_and_buffs()
    {
        var roster = new HarbingerRoster(); var lease = Guid.NewGuid(); roster.Attach(lease, Now);
        roster.Activate(lease, Source(), 100, Now); roster.Step(lease, Now.AddSeconds(4), true, false);
        Assert.Empty(roster.Read(lease, [], Now.AddMilliseconds(4100)));
        Assert.Empty(roster.Step(lease, Now.AddSeconds(5), true, false));
        roster.Activate(lease, Source(), 100, Now.AddSeconds(6));
        roster.Clear(lease);
        Assert.Empty(roster.Read(lease, [20105], Now.AddSeconds(6)));
        roster.Activate(lease, Source(), 100, Now.AddSeconds(7));
        roster.Release(lease, false, Now.AddSeconds(7)); roster.Attach(lease, Now.AddSeconds(8));
        Assert.Empty(roster.Read(lease, [20105], Now.AddSeconds(8)));
    }

    [Fact]
    public void Focus_does_not_start_until_channelled_and_resummoning_replaces_its_cycle()
    {
        var roster = new HarbingerRoster(); var lease = Guid.NewGuid(); roster.Attach(lease, Now);
        roster.Activate(lease, Source(true), 100, Now);
        Assert.Empty(Assert.Single(roster.Step(lease, Now.AddSeconds(5), true, false)).Change.Started);
        Assert.Single(Assert.Single(roster.Step(lease, Now.AddSeconds(5), true, true)).Change.Started);
        var old = Assert.Single(roster.Read(lease, [20105], Now.AddSeconds(5)));
        roster.Activate(lease, Source(true), 100, Now.AddMilliseconds(5100));
        Assert.NotEqual(old.Id, Assert.Single(roster.Read(lease, [20105], Now.AddMilliseconds(5100))).Id);
        Assert.False(roster.Contains(lease, old.Id));
        Assert.Empty(roster.Buffs(lease, Now.AddSeconds(6)));
    }

    [Fact]
    public void Backwards_utc_observation_does_not_count_the_same_time_twice()
    {
        var roster = new HarbingerRoster(); var lease = Guid.NewGuid(); roster.Attach(lease, Now);
        Assert.Equal(5, roster.Time(lease, Now.AddSeconds(5)));
        Assert.Equal(5, roster.Time(lease, Now.AddSeconds(4)));
        Assert.Equal(5, roster.Time(lease, Now.AddSeconds(5)));
    }
}

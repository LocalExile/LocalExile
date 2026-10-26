namespace GameServer.Gateway.Tests;

public sealed class HarbingerCyclesTests
{
    static HarbingerCompanionCatalog.BuffSkill Skill(int effect = 1, int castMs = 700, int durationMs = 4000,
        int cooldownMs = 8000, bool suspend = true) =>
        new(effect, $"FixtureHarbinger{effect}", 1, castMs, durationMs, cooldownMs, suspend, (ushort)effect,
            $"fixture_buff_{effect}", effect, new Dictionary<uint, int>(), new Dictionary<uint, int>());

    [Fact]
    public void Spawn_starts_initial_cooldown_and_the_companions_own_recovery_halves_it()
    {
        var cycles = new HarbingerCycles([Skill()], startedAt: 10, ownCooldownRecoveryPct: 100);
        Assert.Empty(cycles.Advance(13.999, true).Started);
        var cast = Assert.Single(cycles.Advance(14, true).Started);
        Assert.Equal(14, cast.StartedAt);
        Assert.Equal(14.7, cast.CompletesAt, 8);
        Assert.Same(cast, cycles.CurrentCast);
        Assert.True(cycles.IsCasting);
        Assert.Empty(cycles.Active(14));
        var ordinaryRecovery = new HarbingerCycles([Skill()], 10, 0);
        Assert.Empty(ordinaryRecovery.Advance(14, true).Started);
        Assert.Single(ordinaryRecovery.Advance(18, true).Started);
    }

    [Fact]
    public void Cast_completion_and_buff_deadline_use_scheduled_time_and_do_not_add_cast_time_to_the_cycle()
    {
        var cycles = new HarbingerCycles([Skill()], 0, 100);
        cycles.Advance(4, true);
        Assert.Empty(cycles.Advance(4.699, true).Completed);
        var buff = Assert.Single(cycles.Advance(4.7, true).Completed);
        Assert.Equal(4.7, buff.StartedAt, 8);
        Assert.Equal(8.7, buff.EndsAt, 8);
        Assert.Single(cycles.Active(8.699));
        Assert.Empty(cycles.Active(8.7));
        Assert.Empty(cycles.Advance(11.999, true).Started);
        Assert.Equal(12, Assert.Single(cycles.Advance(12, true).Started).StartedAt);
        // Cast start 4 -> 12 is eight seconds; not 8.7. The initial 0.7 recovered before the buff was active.
    }

    [Fact]
    public void Cooldown_continues_through_a_buff_when_the_suspension_flag_is_false()
    {
        var cycles = new HarbingerCycles([Skill(suspend: false)], 0, 100);
        cycles.Advance(4, true);
        cycles.Advance(4.7, true);
        Assert.Equal(8, Assert.Single(cycles.Advance(8, true).Started).StartedAt);
    }

    [Fact]
    public void Time_harbinger_keeps_its_eight_second_buff_and_five_second_recovery_separate()
    {
        var cycles = new HarbingerCycles([Skill(durationMs: 8000, cooldownMs: 10000)], 0, 100);
        cycles.Advance(5, true);
        var buff = Assert.Single(cycles.Advance(5.7, true).Completed);
        Assert.Equal(13.7, buff.EndsAt, 8);
        Assert.Empty(cycles.Advance(17.999, true).Started);
        Assert.Single(cycles.Advance(18, true).Started); // 5 + 8 + 5, no additional cast-time penalty
    }

    [Fact]
    public void Two_arcane_spells_share_a_casting_slot_but_recover_independently()
    {
        var cycles = new HarbingerCycles([Skill(1), Skill(2)], 0, 100);
        Assert.Equal(1, Assert.Single(cycles.Advance(4, true).Started).Skill.Effect);
        Assert.Empty(cycles.Advance(4.5, true).Started);
        var firstCompletion = cycles.Advance(4.7, true);
        Assert.Equal(1, Assert.Single(firstCompletion.Completed).Skill.Effect);
        Assert.Equal(2, Assert.Single(firstCompletion.Started).Skill.Effect);
        var secondCompletion = cycles.Advance(5.4, true);
        Assert.Equal(2, Assert.Single(secondCompletion.Completed).Skill.Effect);
        Assert.Equal(2, cycles.Active(5.4).Count);
        Assert.Equal(1, Assert.Single(cycles.Advance(12, true).Started).Skill.Effect);
        var next = cycles.Advance(12.7, true);
        Assert.Equal(1, Assert.Single(next.Completed).Skill.Effect);
        Assert.Equal(2, Assert.Single(next.Started).Skill.Effect);
    }

    [Fact]
    public void Channel_gate_blocks_new_casts_without_restarting_cooldown_or_cancelling_an_existing_cast()
    {
        var cycles = new HarbingerCycles([Skill()], 0, 100);
        Assert.Empty(cycles.Advance(10, false).Started);
        Assert.Equal(10, Assert.Single(cycles.Advance(10, true).Started).StartedAt);
        var completion = cycles.Advance(10.7, false);
        Assert.Single(completion.Completed);
        Assert.Single(cycles.Active(11));
        Assert.Empty(cycles.Advance(30, false).Started);
        Assert.Equal(30, Assert.Single(cycles.Advance(30, true).Started).StartedAt);
    }

    [Fact]
    public void Delayed_poll_does_not_refresh_an_expired_buff_or_replay_missed_casts()
    {
        var cycles = new HarbingerCycles([Skill()], 0, 100);
        cycles.Advance(4, true);
        var late = cycles.Advance(100, true);
        var expired = Assert.Single(late.Completed);
        Assert.Equal(4.7, expired.StartedAt, 8);
        Assert.Equal(8.7, expired.EndsAt, 8);
        Assert.Empty(cycles.Active(100));
        Assert.Equal(100, Assert.Single(late.Started).StartedAt);
        Assert.Empty(cycles.Advance(100, true).Started);
        Assert.Empty(cycles.Advance(100, true).Completed);
    }

    [Fact]
    public void Identical_clock_values_neither_expire_buffs_nor_start_a_second_cast()
    {
        var cycles = new HarbingerCycles([Skill(1), Skill(2)], 0, 100);
        cycles.Advance(4, true);
        Assert.Empty(cycles.Advance(4, true).Started);
        cycles.Advance(4.7, true);
        var before = Assert.Single(cycles.Active(4.7));
        var unchanged = cycles.Advance(4.7, true);
        Assert.Empty(unchanged.Started);
        Assert.Empty(unchanged.Completed);
        Assert.Same(before, Assert.Single(cycles.Active(4.7)));
        Assert.Equal(2, cycles.CurrentCast!.Skill.Effect);
    }

    [Fact]
    public void Cooldown_finished_during_a_long_cast_is_not_recreated_when_its_buff_starts()
    {
        var cycles = new HarbingerCycles([Skill(castMs: 2000, cooldownMs: 1000)], 0, 0);
        cycles.Advance(1, true);
        var next = cycles.Advance(3, true);
        Assert.Single(next.Completed);
        Assert.Single(next.Started);
        Assert.Single(cycles.Active(3));
        // Suspension affects remaining recovery; an extra "cannot cast while active" rule would belong to AI policy.
    }

    [Fact]
    public void Instant_zero_cooldown_skills_still_start_at_most_once_at_one_timestamp()
    {
        var cycles = new HarbingerCycles([Skill(castMs: 0, durationMs: 0, cooldownMs: 0)], 0, 100);
        var first = cycles.Advance(0, true);
        Assert.Single(first.Started);
        Assert.Single(first.Completed);
        Assert.False(cycles.IsCasting);
        Assert.Empty(cycles.Active(0));
        Assert.Empty(cycles.Advance(0, true).Started);
        Assert.Single(cycles.Advance(1, true).Started);
    }

    [Fact]
    public void Clear_discards_pending_completion_and_effects_and_requires_a_new_instance_to_resummon()
    {
        var cycles = new HarbingerCycles([Skill()], 0, 100);
        cycles.Advance(4, true);
        cycles.Clear();
        Assert.Null(cycles.CurrentCast);
        Assert.Empty(cycles.Advance(5, true).Completed);
        Assert.Empty(cycles.Advance(100, true).Started);
        Assert.Empty(cycles.Active(100));
    }

    [Fact]
    public void Invalid_clocks_and_duplicate_spells_are_rejected_and_nonpositive_recovery_does_not_mean_instant_casts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HarbingerCycles([Skill()], double.NaN, 100));
        Assert.Throws<ArgumentException>(() => new HarbingerCycles([Skill(), Skill()], 0, 100));
        var cycles = new HarbingerCycles([Skill()], 2, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => cycles.Advance(1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => cycles.Advance(double.PositiveInfinity, true));
        Assert.Empty(new HarbingerCycles([Skill()], 0, -100).Advance(100, true).Started);
    }
}

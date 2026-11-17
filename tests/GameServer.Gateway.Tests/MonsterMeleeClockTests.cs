namespace GameServer.Gateway.Tests;

public sealed class MonsterMeleeClockTests
{
    [Fact]
    public void Melee_contacts_follow_native_clip_clocks_and_published_action_speed_changes()
    {
        var bite = new SkillCatalog.MonsterClipContact("attack", 0, 1766, 1099, null) { ClockMetadataAvailable = true };
        // Live native raw speed is 1 through the long-period range, then L/T. Quick's published period is 1388 ms,
        // although the unrounded server stat is 2.4 / 1.3 / 1.33 seconds. Action speed is applied after this mapping.
        foreach (double period in new[] { 3.6, 2.4, 1.8, 1.766 })
            Assert.Equal(1.099, Game.MonsterMeleeContactSeconds(bite, true, period)!.Value, 9);
        foreach (double period in new[] { 2.4 / 1.3 / 1.33, 1.388, .8 })
        {
            double native = Game.MonsterMeleeContactSeconds(bite, true, period)!.Value;
            Assert.Equal(1.099 * Math.Round(period * 1000) / 1766, native, 9);
            foreach (double factor in new[] { .5, 1, 1.5 })
                Assert.Equal(10 + native / factor, new ActionTimeClock(10, factor).Deadline(native), 9);
        }
        Assert.Equal(2.4 * 1099 / 1766, Game.MonsterMeleeContactSeconds(bite, false, 2.4)!.Value, 9);
        Assert.Null(Game.MonsterMeleeContactSeconds(bite with { ClockMetadataAvailable = false }, true, 2.4));
        Assert.Null(Game.MonsterMeleeContactSeconds(bite, null, 2.4));
        Assert.Null(Game.MonsterMeleeContactSeconds(null, true, 2.4));

        // Actual Mummy.ao, not Mummy_no_amb.ao: two sequence-selected clips with different slow spans. The native
        // eight-times cap leaves an idle tail, so raising the period from 2.4 to 5 seconds does not delay contact.
        var mummy = new SkillCatalog.MonsterClipContact("attack1", 0, 1166, 566, null)
            { ClockMetadataAvailable = true, SlowBeginMs = 327, SlowEndMs = 502 };
        var second = mummy with { Animation = "attack2", Index = 1, ContactMs = 632, SlowBeginMs = 387, SlowEndMs = 526 };
        Assert.Equal(.900, Game.MonsterMeleeContactSeconds(mummy, true, 1.5)!.Value, 9);
        Assert.Equal(.966, Game.MonsterMeleeContactSeconds(second, true, 1.5)!.Value, 9);
        foreach (double period in new[] { 2.4, 5.0 })
        {
            Assert.Equal(1.791, Game.MonsterMeleeContactSeconds(mummy, true, period)!.Value, 9);
            Assert.Equal(1.605, Game.MonsterMeleeContactSeconds(second, true, period)!.Value, 9);
        }
        Assert.Equal(.566 * .8 / 1.166, Game.MonsterMeleeContactSeconds(mummy, true, .8)!.Value, 9);
        // Events before, inside and after the span; an explicit budget can restore linear playback despite the
        // initial clamp. These synthetic clips exercise boundaries independent of the currently installed data.
        Assert.Equal(.1, AnimationEventClock.Seconds(1000, 100, 2, true, 200, 400)!.Value, 9);
        Assert.Equal(.8, AnimationEventClock.Seconds(1000, 300, 2, true, 200, 400)!.Value, 9);
        Assert.Equal(1.6, AnimationEventClock.Seconds(1000, 600, 2, true, 200, 400)!.Value, 9);
        Assert.Equal(.9, AnimationEventClock.Seconds(1000, 600, 1.5, true, 200, 400, 2)!.Value, 9);
        Assert.Equal(3.3, AnimationEventClock.Seconds(1000, 600, 5, false, 200, 400)!.Value, 9);
        Assert.Null(AnimationEventClock.Seconds(1000, 600, 2, true, 400, 200));
        Assert.Null(AnimationEventClock.Seconds(1000, 600, double.NaN, true));

        foreach (bool lockstep in new[] { false, true })
        {
            var conn = new ClientState(new(1, "melee-clock", 300, 300, 100, 100, null, []), 1, 1) { LockstepClient = lockstep };
            var clock = new ActionTimeClock(10, 1);
            var monster = new SpawnedMonster("clock", new(105, 100, 1), 60, 2, 100)
            {
                ActionGeneration = 1, SwingGeneration = 1, MeleeClock = (1, clock, 1.099, 2.4, conn.LockstepActionTail),
                BusyUntil = 12.4 + conn.LockstepActionTail,
            };
            conn.Monsters[1] = monster;
            Assert.NotNull(monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 1, 10.4, _ => 0, _ => 0, stated: 30));
            Game.MonsterAilmentChangesLocked(conn, 10.4);
            Assert.Equal(-30, monster.Ailments.SentActionSpeed);
            Assert.Equal(.7, clock.Rate, 9);
            Assert.Equal(10.4 + 2 / .7 + conn.LockstepActionTail, monster.BusyUntil, 9);
            Assert.Equal(10.4 + .699 / .7, clock.Deadline(1.099), 9);
            Assert.True(clock.Elapsed(11.39) < 1.099);

            // Freeze remains a separate admission/contact cancellation rule. It must neither publish -100 nor
            // overwrite the ongoing clock's chill rate; expiry returns admission to the still-active chill.
            Assert.NotNull(monster.Ailments.Freeze(.5, 10.6, _ => 0, _ => 0));
            Game.MonsterAilmentChangesLocked(conn, 10.6);
            Assert.Equal(0, Game.MonsterActionFactor(monster, 10.6));
            Assert.Equal(.7, clock.Rate, 9);
            Game.MonsterAilmentChangesLocked(conn, 11.1);
            Assert.Equal(.7, Game.MonsterActionFactor(monster, 11.1), 9);

            // Chill expiry settles one second at .7 before restoring 1, including the remaining recovery.
            Game.MonsterAilmentChangesLocked(conn, 11.4);
            Assert.Equal(0, monster.Ailments.SentActionSpeed);
            Assert.Equal(1.1, clock.Elapsed(11.4), 9);
            Assert.Equal(12.7 + conn.LockstepActionTail, monster.BusyUntil, 9);
            Assert.True(clock.Elapsed(11.4) >= 1.099);
            // A canceled generation cannot retime a successor's recovery (including a boss or flinch action).
            monster.ActionGeneration = 2;
            monster.SwingGeneration = 2;
            monster.BusyUntil = 99;
            Game.RetimeMonsterMeleeClockLocked(monster, 11.5, -50);
            Assert.Equal(99, monster.BusyUntil);
            Assert.Equal(1, clock.Rate);
            Assert.False(Game.BeginMonsterMeleeClockLocked(conn, monster, 1, 0x8000, 10, 11.5));
            Assert.Same(clock, monster.MeleeClock!.Value.Clock);
            Assert.Equal(99, monster.BusyUntil);
        }

        // Progress is determined by area time, so a paused clock consumes nothing. A zero published rate can also
        // suspend and resume without retroactively applying the new rate to the interval that already elapsed.
        var stopped = new ActionTimeClock(0, 1);
        stopped.SetRate(.4, 0);
        Assert.Equal(.4, stopped.Elapsed(2), 9);
        Assert.Equal(double.PositiveInfinity, stopped.Deadline(1));
        stopped.SetRate(2, 1);
        Assert.Equal(2.6, stopped.Deadline(1), 9);
        Assert.Equal(.4, stopped.Elapsed(2), 9);
    }
}

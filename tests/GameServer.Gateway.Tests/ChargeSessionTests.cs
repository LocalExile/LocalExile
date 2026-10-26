// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChargeSessionTests
{
    static readonly WeaponProfile Unarmed = new(false, false, false, false, false, Unarmed: true, Melee: true);
    static readonly DateTimeOffset Epoch = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    sealed class Character(params (string Name, int Value)[] stats)
    {
        public Dictionary<string, int> Stats { get; } = stats.ToDictionary(s => s.Name, s => s.Value);
        public int Reads { get; private set; }
        public int Get(string name) { Reads++; return Stats.GetValueOrDefault(name); }
    }

    static void Accept(ChargeSession s, ChargeEvent e, double at, Character c, WeaponProfile? weapon = null) =>
        Assert.True(s.EnqueueAccepted(e, at, c.Get, weapon ?? Unarmed));

    static ChargeSession.StepResult Step(ChargeSession s, double now, Character c, Random? rng = null, bool alive = true,
        bool moving = false, bool inTown = false, bool observe = true)
    {
        if (observe) s.Observe(c.Get);
        return s.Step(now, alive, moving, inTown, rng ?? new ScriptedRandom());
    }

    // Each test owns its allocator; no shared counter race when xUnit classes run in parallel.
    static Func<ushort> Allocator() { ushort next = 0; return () => ++next; }

    [Fact]
    public void A_kill_with_a_full_chance_gains_a_charge_and_shows_its_buff()
    {
        var s = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        var next = Allocator();
        Step(s, 0, c);
        Accept(s, new ChargeEvent.Kill(MonsterRarity.Normal), 1, c);
        var r = Step(s, 1, c);
        Assert.True(r.CountsChanged);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        var shown = Assert.Single(s.Presentation(1, next));
        Assert.Equal(ChargeType.Frenzy, shown.Type);
        Assert.Equal(0, shown.Remove);
        Assert.Equal(1, shown.Count);
        Assert.Equal(10, shown.Remaining!.Value, 6);
        Assert.False(Step(s, 1.05, c).CountsChanged);
        Assert.Empty(s.Presentation(1.05, next));
    }

    [Fact]
    public void A_failed_roll_gains_nothing_and_the_rng_decides()
    {
        var s = new ChargeSession();
        var c = new Character(("add_power_charge_on_critical_strike_%", 40));
        var rng = new ScriptedRandom(40, 39);
        Accept(s, new ChargeEvent.Hit(true, true, null, MonsterRarity.Normal), 0, c);
        Assert.False(Step(s, 0, c, rng).CountsChanged);
        Accept(s, new ChargeEvent.Hit(true, true, null, MonsterRarity.Normal), 0.05, c);
        Assert.True(Step(s, 0.05, c, rng).CountsChanged);
        Assert.Equal(1, s.Effective(ChargeType.Power));
        Assert.Equal(0, rng.Left);
    }

    [Fact]
    public void Charges_cap_at_the_maximum_and_a_gain_there_restarts_the_buff_timer()
    {
        var s = new ChargeSession();
        var c = new Character(("max_endurance_charges", 1));
        var next = Allocator();
        Accept(s, new ChargeEvent.Grant(ChargeType.Endurance, 9, "test"), 0, c);
        Step(s, 0, c);
        Assert.Equal(4, s.Effective(ChargeType.Endurance));
        var first = Assert.Single(s.Presentation(0, next));
        Accept(s, new ChargeEvent.Grant(ChargeType.Endurance, 1, "test"), 6, c);
        Assert.False(Step(s, 6, c).CountsChanged);
        var refreshed = Assert.Single(s.Presentation(6, next));
        Assert.Equal(first.Add, refreshed.Remove);
        Assert.Equal(4, refreshed.Count);
        Assert.Equal(10, refreshed.Remaining!.Value, 6);
        Assert.Equal(16, s.State.ExpiresAt(ChargeType.Endurance), 6);
    }

    [Fact]
    public void An_expired_stack_is_removed_before_a_new_gain_even_when_the_ticker_is_late()
    {
        var s = new ChargeSession();
        var c = new Character();
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 3, "old"), 0, c);
        Step(s, 0, c);
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 1, "new"), 10.01, c);
        var r = Step(s, 10.1, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        Assert.Equal(20.01, s.State.ExpiresAt(ChargeType.Frenzy), 6);
        Assert.Contains("frenzy charges expired", r.Log);
        Assert.Equal(9.91, Assert.Single(s.Presentation(10.1, Allocator())).Remaining!.Value, 6);
    }

    [Fact]
    public void A_delayed_event_that_already_expired_cannot_resurrect_charges()
    {
        var s = new ChargeSession();
        var c = new Character();
        Accept(s, new ChargeEvent.Grant(ChargeType.Power, 1, "delayed"), 0, c);
        Step(s, 10, c);
        Assert.Equal(0, s.Effective(ChargeType.Power));
        Assert.Empty(s.Presentation(10, Allocator()));
    }

    [Fact]
    public void Charges_expire_on_the_game_clock_all_at_once()
    {
        var s = new ChargeSession();
        var c = new Character(("charge_duration_+%", 50));
        var next = Allocator();
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 2, "test"), 100, c);
        Step(s, 100, c);
        var shown = Assert.Single(s.Presentation(100, next));
        for (int i = 0; i < 5; i++) Assert.False(Step(s, 114.95, c).CountsChanged);
        Assert.Equal(2, s.Effective(ChargeType.Frenzy));
        var r = Step(s, 115, c);
        Assert.True(r.CountsChanged);
        Assert.Contains("frenzy charges expired", r.Log);
        Assert.Equal(0, s.Effective(ChargeType.Frenzy));
        var gone = Assert.Single(s.Presentation(115, next));
        Assert.Equal(shown.Add, gone.Remove);
        Assert.Equal(0, gone.Add);
        Assert.Equal(0, gone.Count);
    }

    [Fact]
    public void Death_takes_the_charges_and_drops_pending_events()
    {
        var s = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        var next = Allocator();
        Accept(s, new ChargeEvent.Grant(ChargeType.Power, 3, "test"), 0, c);
        Step(s, 0, c);
        s.Presentation(0, next);
        Accept(s, new ChargeEvent.Kill(MonsterRarity.Normal), 1, c);
        var r = Step(s, 1, c, alive: false);
        Assert.True(r.CountsChanged);
        Assert.Contains("charges lost on death", r.Log);
        Assert.Equal(0, s.Effective(ChargeType.Power));
        Assert.Equal(0, s.Effective(ChargeType.Frenzy));
        Assert.Single(s.Presentation(1, next));
        Assert.False(s.Pending);
        Accept(s, new ChargeEvent.Kill(MonsterRarity.Normal), 2, c);
        Step(s, 2, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
    }

    [Fact]
    public void Minimum_charges_show_without_a_timer_and_gains_go_on_top()
    {
        var s = new ChargeSession();
        var c = new Character(("base_minimum_frenzy_charges", 1));
        var next = Allocator();
        Step(s, 0, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        var minimum = Assert.Single(s.Presentation(0, next));
        Assert.Equal(1, minimum.Count);
        Assert.Null(minimum.Remaining);
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 1, "test"), 1, c);
        Step(s, 1, c);
        Assert.Equal(2, s.Effective(ChargeType.Frenzy));
        Assert.Equal(10, Assert.Single(s.Presentation(1, next)).Remaining!.Value, 6);
        Step(s, 11, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        Assert.Null(Assert.Single(s.Presentation(11, next)).Remaining);
    }

    [Fact]
    public void A_lower_maximum_takes_effect_when_the_stats_change_and_clamps_the_charges()
    {
        var s = new ChargeSession();
        var c = new Character(("max_power_charges", 2));
        Accept(s, new ChargeEvent.Grant(ChargeType.Power, 5, "test"), 0, c);
        Step(s, 0, c);
        Assert.Equal(5, s.Effective(ChargeType.Power));
        c.Stats.Remove("max_power_charges");
        Assert.False(Step(s, 0.1, c, observe: false).CountsChanged);
        Assert.True(Step(s, 0.15, c).CountsChanged);
        Assert.Equal(3, s.Effective(ChargeType.Power));
    }

    [Fact]
    public void Accepted_events_capture_player_skill_duration_and_weapon_sources_before_refund_or_swap()
    {
        var s = new ChargeSession();
        var c = new Character(("gain_power_charge_on_critical_strike_with_wands_%", 50), ("charge_duration_+%", 50));
        var skill = new Dictionary<string, int> { ["gain_power_charge_on_critical_strike_with_wands_%"] = 50 };
        var weapon = Unarmed with { Unarmed = false, Melee = false, Wand = true };
        Accept(s, new ChargeEvent.Hit(true, false, null, MonsterRarity.Normal, Skill: n => skill.GetValueOrDefault(n)), 1, c, weapon);
        c.Stats.Clear();
        skill.Clear();
        weapon = Unarmed;
        Step(s, 1.5, c);
        Assert.Equal(1, s.Effective(ChargeType.Power));
        Assert.Equal(16, s.State.ExpiresAt(ChargeType.Power), 6);
        // Refunding/swapping before a later hit really does remove its chance.
        Accept(s, new ChargeEvent.Hit(true, false, null, MonsterRarity.Normal), 2, c, weapon);
        Assert.False(Step(s, 2, c).CountsChanged);
        Assert.Equal(16, s.State.ExpiresAt(ChargeType.Power), 6);
    }

    [Fact]
    public void A_later_source_cannot_retroactively_grant_a_charge_for_an_earlier_hit()
    {
        var s = new ChargeSession();
        var c = new Character();
        Accept(s, new ChargeEvent.Hit(true, false, null, MonsterRarity.Normal), 1, c);
        c.Stats["gain_power_charge_on_critical_strike_with_wands_%"] = 100;
        Step(s, 1.1, c);
        Assert.Equal(0, s.Effective(ChargeType.Power));
    }

    [Fact]
    public void The_ticker_uses_frozen_inputs_without_re_reading_character_stats()
    {
        var s = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        s.Observe(c.Get);
        Accept(s, new ChargeEvent.Kill(MonsterRarity.Normal), 1, c);
        int reads = c.Reads;
        Step(s, 1, c, observe: false);
        Step(s, 2, c, observe: false);
        Assert.Equal(reads, c.Reads);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
    }

    [Fact]
    public void Death_clears_at_once_even_while_the_clock_is_paused()
    {
        var s = new ChargeSession();
        var c = new Character();
        var next = Allocator();
        Accept(s, new ChargeEvent.Grant(ChargeType.Endurance, 3, "test"), 0, c);
        Step(s, 0, c);
        s.Presentation(0, next);
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 1, "waiting"), 0.5, c);
        Assert.True(s.Die(0.5));
        Assert.False(s.Pending);
        Assert.Empty(s.State.Carry(0.5));
        Assert.True(Step(s, 0.5, c).CountsChanged);
        Assert.Equal(0, s.Effective(ChargeType.Endurance));
        Assert.Equal(0, Assert.Single(s.Presentation(0.5, next)).Count);
        Assert.False(s.EnqueueAccepted(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "late"), 0.4, c.Get, Unarmed));
    }

    [Fact]
    public void Being_hit_starts_recent_gains_at_the_hit_time_and_towns_gain_nothing_over_time()
    {
        var s = new ChargeSession();
        var c = new Character(("gain_endurance_charge_per_second_if_have_been_hit_recently", 1), ("gain_X_frenzy_charges_per_second", 1));
        Step(s, 0, c, inTown: true);
        Step(s, 2, c, inTown: true);
        Assert.Equal(0, s.Effective(ChargeType.Frenzy));
        Step(s, 3, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        Accept(s, new ChargeEvent.WhenHit(), 3.5, c);
        Step(s, 3.5, c);
        Assert.Equal(3.5, s.LastHitAt);
        Step(s, 4.49, c);
        Assert.Equal(0, s.Effective(ChargeType.Endurance));
        Step(s, 4.5, c);
        Assert.Equal(1, s.Effective(ChargeType.Endurance));
        Step(s, 7.5, c);
        Assert.Equal(3, s.Effective(ChargeType.Endurance));
        Step(s, 9, c);
        Assert.Equal(3, s.Effective(ChargeType.Endurance));
    }

    [Fact]
    public void Delayed_periodic_gains_keep_their_original_due_times()
    {
        var s = new ChargeSession();
        var c = new Character(("body_armour_implicit_gain_power_charge_every_x_ms", 11_000));
        Step(s, 0, c);
        Step(s, 20, c);
        Assert.Equal(1, s.Effective(ChargeType.Power));
        Assert.Equal(21, s.State.ExpiresAt(ChargeType.Power), 6);
        Step(s, 21, c);
        Assert.Equal(0, s.Effective(ChargeType.Power));
    }

    [Fact]
    public void A_gain_can_go_straight_to_maximum_and_per_charge_duration_counts_that_maximum()
    {
        var s = new ChargeSession();
        var c = new Character(("gain_maximum_frenzy_charges_on_frenzy_charge_gained_%_chance", 10),
            ("frenzy_charge_duration_+%_per_frenzy_charge", 10));
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 1, "test"), 0, c);
        Step(s, 0, c, new ScriptedRandom(9));
        Assert.Equal(3, s.Effective(ChargeType.Frenzy));
        Assert.Equal(13, s.State.ExpiresAt(ChargeType.Frenzy), 6);
        Accept(s, new ChargeEvent.Set(ChargeType.Frenzy, 0), 1, c);
        Accept(s, new ChargeEvent.Grant(ChargeType.Frenzy, 1, "test"), 1, c);
        Step(s, 1, c, new ScriptedRandom(10));
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        Assert.Equal(12, s.State.ExpiresAt(ChargeType.Frenzy), 6);
    }

    [Fact]
    public void The_dev_command_sets_saturates_adds_and_clears()
    {
        var s = new ChargeSession();
        var c = new Character();
        Accept(s, new ChargeEvent.Set(ChargeType.Power, 2), 0, c);
        Accept(s, new ChargeEvent.Set(ChargeType.Endurance, int.MaxValue), 0, c);
        Accept(s, new ChargeEvent.Grant(ChargeType.Power, int.MaxValue, "admin add"), 0, c);
        Step(s, 0, c);
        Assert.Equal(3, s.Effective(ChargeType.Power));
        Assert.Equal(3, s.Effective(ChargeType.Endurance));
        Accept(s, new ChargeEvent.Set(null, 0), 1, c);
        Assert.True(Step(s, 1, c).CountsChanged);
        Assert.All(Charges.Types, t => Assert.Equal(0, s.Effective(t)));
    }

    [Fact]
    public void An_immediate_area_transfer_flushes_a_pending_event_once_and_preserves_its_deadline()
    {
        var old = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        Step(old, 0, c);
        Accept(old, new ChargeEvent.Kill(MonsterRarity.Normal), 1, c);
        var transfer = old.CloseAndCarry(1, Epoch, true, false, false, new ScriptedRandom());
        Assert.True(old.Closed);
        Assert.False(old.Pending);
        Assert.False(old.EnqueueAccepted(new ChargeEvent.Kill(MonsterRarity.Normal), 2, c.Get, Unarmed));
        var dest = new ChargeSession();
        dest.Observe(c.Get);
        Assert.True(dest.Restore(transfer, 0.25, Epoch.AddSeconds(3)));
        Assert.Equal(1, dest.Effective(ChargeType.Frenzy));
        Assert.Equal(7, dest.State.Remaining(ChargeType.Frenzy, 0.25), 6);
        Assert.False(dest.Restore(transfer, 0.25, Epoch.AddSeconds(3)));
        var duplicate = new ChargeSession();
        Assert.False(duplicate.Restore(transfer, 0, Epoch.AddSeconds(3)));
        old.Step(2, true, false, false, new ScriptedRandom());
        Assert.Equal(1, old.Effective(ChargeType.Frenzy));
        var buff = Assert.Single(dest.Presentation(0.25, Allocator()));
        Assert.Equal(7, buff.Remaining!.Value, 6);
        Step(dest, 7.25, c);
        Assert.Equal(0, dest.Effective(ChargeType.Frenzy));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void A_carry_expired_during_loading_is_not_restored(double loadSeconds)
    {
        var old = new ChargeSession();
        var c = new Character();
        Accept(old, new ChargeEvent.Grant(ChargeType.Power, 3, "test"), 0, c);
        var transfer = old.CloseAndCarry(0, Epoch, true, false, false, new ScriptedRandom());
        var dest = new ChargeSession();
        dest.Observe(c.Get);
        Assert.True(dest.Restore(transfer, 0, Epoch.AddSeconds(loadSeconds)));
        Assert.Equal(0, dest.Effective(ChargeType.Power));
        Assert.Empty(dest.Presentation(0, Allocator()));
    }

    [Fact]
    public void Carry_clamps_to_destination_maximum_and_a_second_close_carries_nothing()
    {
        var old = new ChargeSession();
        var c = new Character(("max_power_charges", 2));
        old.Observe(c.Get);
        Accept(old, new ChargeEvent.Grant(ChargeType.Power, 5, "test"), 0, c);
        var transfer = old.CloseAndCarry(0, Epoch, true, false, false, new ScriptedRandom());
        Assert.Equal(5, old.Effective(ChargeType.Power));
        var dest = new ChargeSession();
        dest.Observe(new Character(("max_power_charges", -1)).Get);
        Assert.True(dest.Restore(transfer, 0, Epoch));
        Assert.Equal(2, dest.Effective(ChargeType.Power));
        var empty = old.CloseAndCarry(1, Epoch.AddSeconds(1), true, false, false, new ScriptedRandom());
        var next = new ChargeSession();
        Assert.True(next.Restore(empty, 0, Epoch.AddSeconds(1)));
        Assert.Equal(0, next.Effective(ChargeType.Power));
    }

    [Fact]
    public void The_session_rejects_out_of_order_events_and_defers_future_ones()
    {
        var s = new ChargeSession();
        var c = new Character();
        Step(s, 2, c);
        Assert.False(s.EnqueueAccepted(new ChargeEvent.Grant(ChargeType.Power, 1, "old"), 1, c.Get, Unarmed));
        Accept(s, new ChargeEvent.Grant(ChargeType.Power, 1, "future"), 3, c);
        Step(s, 2.5, c);
        Assert.True(s.Pending);
        Assert.Equal(0, s.Effective(ChargeType.Power));
        Step(s, 3, c);
        Assert.False(s.Pending);
        Assert.Equal(1, s.Effective(ChargeType.Power));
    }

    [Fact]
    public void A_new_player_object_gets_every_held_buff_again()
    {
        var s = new ChargeSession();
        var c = new Character();
        s.State.Restore([new(ChargeType.Frenzy, 2, 6.5), new(ChargeType.Power, 1, 3)], 0.25);
        Assert.True(Step(s, 0.3, c).CountsChanged);
        var buffs = s.Presentation(0.3, Allocator());
        Assert.Equal([ChargeType.Frenzy, ChargeType.Power], buffs.Select(b => b.Type));
        Assert.Equal(6.45, buffs[0].Remaining!.Value, 6);
        Assert.All(buffs, b => Assert.Equal(0, b.Remove));
    }
}

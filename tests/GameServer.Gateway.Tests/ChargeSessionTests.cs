using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChargeSessionTests
{
    static readonly WeaponProfile Unarmed = new(false, false, false, false, false, Unarmed: true, Melee: true);

    sealed class Character(params (string Name, int Value)[] stats)
    {
        public Dictionary<string, int> Stats { get; } = stats.ToDictionary(s => s.Name, s => s.Value);
        public int Reads { get; private set; }
        public int Get(string name) => Stats.GetValueOrDefault(name);
        // The ticker's stats for waiting events: worked out (counted) only then.
        public Func<string, int> Read() { Reads++; return Get; }
    }

    // A ticker step: the character's stats observed (as every stats change does through WithCharges, unless
    // observe is false), and passed in only while events wait.
    static ChargeSession.StepResult Step(ChargeSession s, double now, Character c, Random? rng = null, bool alive = true,
        bool moving = false, bool inTown = false, bool observe = true)
    {
        if (observe) s.Observe(c.Get);
        return s.Step(now, s.Pending ? c.Read() : null, () => Unarmed, alive, moving, inTown, rng ?? new ScriptedRandom());
    }

    static ushort counter;
    static ushort Next() => ++counter;

    [Fact]
    public void A_kill_with_a_full_chance_gains_a_charge_and_shows_its_buff()
    {
        var s = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        Step(s, 0, c);
        s.Enqueue(new ChargeEvent.Kill(MonsterRarity.Normal));
        var r = Step(s, 1, c);
        Assert.True(r.CountsChanged);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        var shown = Assert.Single(s.Presentation(1, Next));
        Assert.Equal(ChargeType.Frenzy, shown.Type);
        Assert.Equal(0, shown.Remove);
        Assert.Equal(1, shown.Count);
        Assert.Equal(10, shown.Remaining!.Value, 6);
        // Nothing changed: nothing to send.
        Assert.False(Step(s, 1.05, c).CountsChanged);
        Assert.Empty(s.Presentation(1.05, Next));
    }

    [Fact]
    public void Converted_charges_show_the_special_buff_and_a_flag_change_swaps_it()
    {
        var s = new ChargeSession();
        var c = new Character((Charges.ConversionFlag(ChargeType.Endurance), 1), ("add_frenzy_charge_on_kill_%_chance", 100));
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, 2, "test"));
        s.Enqueue(new ChargeEvent.Kill(MonsterRarity.Normal));
        var r = Step(s, 0, c);
        Assert.True(s.Converted(ChargeType.Endurance));
        Assert.False(s.MinimumConverted(ChargeType.Endurance));
        Assert.Contains(r.Log, l => l.Contains("brutal charges 0 -> 2/3"));
        Assert.Contains(r.Log, l => l.Contains("frenzy charges 0 -> 1/3"));
        var shown = s.Presentation(0, Next);
        var brutal = Assert.Single(shown, b => b.Type == ChargeType.Endurance);
        Assert.True(brutal.Converted);
        Assert.Equal(2, brutal.Count);
        Assert.False(Assert.Single(shown, b => b.Type == ChargeType.Frenzy).Converted);
        // The belt comes off: the same charges are Endurance Charges again, so the buff is swapped.
        c.Stats.Remove(Charges.ConversionFlag(ChargeType.Endurance));
        Assert.False(Step(s, 0.05, c).CountsChanged);   // the held count did not change
        var swapped = Assert.Single(s.Presentation(0.05, Next));
        Assert.Equal(ChargeType.Endurance, swapped.Type);
        Assert.False(swapped.Converted);
        Assert.Equal(brutal.Add, swapped.Remove);
        Assert.Equal(2, swapped.Count);
        Assert.Empty(s.Presentation(0.1, Next));
    }

    [Fact]
    public void A_failed_roll_gains_nothing_and_the_rng_decides()
    {
        var s = new ChargeSession();
        var c = new Character(("add_power_charge_on_critical_strike_%", 40));
        var rng = new ScriptedRandom(40, 39);
        s.Enqueue(new ChargeEvent.Hit(Critical: true, Spell: true, Melee: null, MonsterRarity.Normal));
        Assert.False(Step(s, 0, c, rng).CountsChanged);
        s.Enqueue(new ChargeEvent.Hit(Critical: true, Spell: true, Melee: null, MonsterRarity.Normal));
        Assert.True(Step(s, 0.05, c, rng).CountsChanged);
        Assert.Equal(1, s.Effective(ChargeType.Power));
        Assert.Equal(0, rng.Left);
    }

    [Fact]
    public void Charges_cap_at_the_maximum_and_a_gain_there_restarts_the_buff_timer()
    {
        var s = new ChargeSession();
        var c = new Character(("max_endurance_charges", 1));   // maximum 4
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, 9, "test"));
        Step(s, 0, c);
        Assert.Equal(4, s.Effective(ChargeType.Endurance));
        var first = Assert.Single(s.Presentation(0, Next));
        Assert.Equal(4, first.Count);
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, 1, "test"));
        Assert.False(Step(s, 6, c).CountsChanged);   // still 4: the stats stay
        var refreshed = Assert.Single(s.Presentation(6, Next));   // the buff goes again with the full time
        Assert.Equal(first.Add, refreshed.Remove);
        Assert.Equal(4, refreshed.Count);
        Assert.Equal(10, refreshed.Remaining!.Value, 6);
        Assert.Equal(16, s.State.ExpiresAt(ChargeType.Endurance), 6);
    }

    [Fact]
    public void Charges_expire_on_the_game_clock_all_at_once()
    {
        var s = new ChargeSession();
        var c = new Character(("charge_duration_+%", 50));   // 15 s
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 2, "test"));
        Step(s, 100, c);
        var shown = Assert.Single(s.Presentation(100, Next));
        // A paused game clock does not move: however long the pause lasts, the steps see the same time.
        for (int i = 0; i < 5; i++) Assert.False(Step(s, 114.95, c).CountsChanged);
        Assert.Equal(2, s.Effective(ChargeType.Frenzy));
        var r = Step(s, 115, c);
        Assert.True(r.CountsChanged);
        Assert.Contains("frenzy charges expired", r.Log);
        Assert.Equal(0, s.Effective(ChargeType.Frenzy));
        var gone = Assert.Single(s.Presentation(115, Next));
        Assert.Equal(shown.Add, gone.Remove);
        Assert.Equal(0, gone.Add);
        Assert.Equal(0, gone.Count);
    }

    [Fact]
    public void A_losing_per_second_jewel_takes_a_charge_each_second_and_keeps_the_minimum()
    {
        // Replica Fragility, Replica Pacifism and Replica Powerlessness: "Lose an Endurance Charge each second",
        // stored as gain_X_endurance_charges_per_second = -1.
        var s = new ChargeSession();
        var c = new Character(("gain_X_endurance_charges_per_second", -1), ("base_minimum_endurance_charges", 1));
        Step(s, 0, c);
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, 3, "test"));
        Step(s, 0.05, c);
        Assert.Equal(3, s.Effective(ChargeType.Endurance));
        Step(s, 1.05, c);
        Assert.Equal(2, s.Effective(ChargeType.Endurance));
        Step(s, 2.05, c);
        Assert.Equal(1, s.Effective(ChargeType.Endurance));
        // The minimum never goes, whatever the jewel takes.
        Step(s, 3.05, c);
        Assert.Equal(1, s.Effective(ChargeType.Endurance));
    }

    [Fact]
    public void Death_takes_the_charges_and_drops_pending_events()
    {
        var s = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Power, 3, "test"));
        Step(s, 0, c);
        s.Presentation(0, Next);
        s.Enqueue(new ChargeEvent.Kill(MonsterRarity.Normal));
        var r = Step(s, 1, c, alive: false);
        Assert.True(r.CountsChanged);
        Assert.Contains("charges lost on death", r.Log);
        Assert.Equal(0, s.Effective(ChargeType.Power));
        Assert.Equal(0, s.Effective(ChargeType.Frenzy));
        Assert.Single(s.Presentation(1, Next));   // the power buff comes off
        Assert.False(s.Pending);
        // Alive again: the next kill gains.
        s.Enqueue(new ChargeEvent.Kill(MonsterRarity.Normal));
        Step(s, 2, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
    }

    [Fact]
    public void Minimum_charges_show_without_a_timer_and_gains_go_on_top()
    {
        var s = new ChargeSession();
        var c = new Character(("base_minimum_frenzy_charges", 1));
        Step(s, 0, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        var minimum = Assert.Single(s.Presentation(0, Next));
        Assert.Equal(1, minimum.Count);
        Assert.Null(minimum.Remaining);
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "test"));
        Step(s, 1, c);
        Assert.Equal(2, s.Effective(ChargeType.Frenzy));
        Assert.Equal(10, Assert.Single(s.Presentation(1, Next)).Remaining!.Value, 6);
        Step(s, 11, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        Assert.Null(Assert.Single(s.Presentation(11, Next)).Remaining);
    }

    [Fact]
    public void A_lower_maximum_takes_effect_when_the_stats_change_and_clamps_the_charges()
    {
        var s = new ChargeSession();
        var c = new Character(("max_power_charges", 2));
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Power, 5, "test"));
        Step(s, 0, c);
        Assert.Equal(5, s.Effective(ChargeType.Power));
        c.Stats.Remove("max_power_charges");   // the item comes off; nothing polls the stats
        Assert.False(Step(s, 0.1, c, observe: false).CountsChanged);
        var r = Step(s, 0.15, c);   // the unequip's stats change observes the new maximum
        Assert.True(r.CountsChanged);
        Assert.Equal(3, s.Effective(ChargeType.Power));
    }

    [Fact]
    public void Stats_are_worked_out_only_for_waiting_events()
    {
        var s = new ChargeSession();
        var c = new Character(("gain_X_frenzy_charges_per_second", 1));
        for (int i = 0; i <= 21; i++) Step(s, i * 0.05, c, observe: i == 0);
        Assert.Equal(0, c.Reads);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));   // the timed gain uses the observed stats
        s.Enqueue(new ChargeEvent.WhenHit());
        Step(s, 1.1, c, observe: false);
        Assert.Equal(1, c.Reads);
        // An event without stats waits for the next step.
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Power, 1, "test"));
        s.Step(1.15, null, () => Unarmed, true, false, false, new ScriptedRandom());
        Assert.True(s.Pending);
        Assert.Equal(0, s.Effective(ChargeType.Power));
    }

    [Fact]
    public void Death_clears_at_once_even_while_the_clock_is_paused()
    {
        // Die, pause, resurrect while paused: the ticker never steps in between, so death itself takes the charges.
        var s = new ChargeSession();
        var c = new Character();
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, 3, "test"));
        Step(s, 0, c);
        s.Presentation(0, Next);
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "waiting"));
        Assert.True(s.Die());
        Assert.False(s.Pending);
        Assert.Empty(s.State.Carry(0.5));   // Resurrect in Town carries nothing
        var r = Step(s, 0.5, c);            // alive again, the same paused time
        Assert.True(r.CountsChanged);
        Assert.Equal(0, s.Effective(ChargeType.Endurance));
        Assert.Equal(0, Assert.Single(s.Presentation(0.5, Next)).Count);   // the icon comes off
    }

    [Fact]
    public void Being_hit_starts_the_hit_recently_gains_and_towns_gain_nothing_over_time()
    {
        var s = new ChargeSession();
        var c = new Character(("gain_endurance_charge_per_second_if_have_been_hit_recently", 1), ("gain_X_frenzy_charges_per_second", 1));
        Step(s, 0, c, inTown: true);
        Step(s, 2, c, inTown: true);
        Assert.Equal(0, s.Effective(ChargeType.Frenzy));
        Step(s, 3, c);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        Assert.Equal(0, s.Effective(ChargeType.Endurance));
        s.Enqueue(new ChargeEvent.WhenHit());
        Step(s, 3.5, c);
        Assert.Equal(3.5, s.LastHitAt);
        Step(s, 4.5, c);
        Assert.Equal(1, s.Effective(ChargeType.Endurance));
        // Four seconds after the hit it is no longer recent.
        Step(s, 7.5, c);
        Assert.Equal(3, s.Effective(ChargeType.Endurance));   // three more seconds: capped at 3
        Step(s, 9, c);
        Assert.Equal(3, s.Effective(ChargeType.Endurance));
    }

    [Fact]
    public void A_gain_can_go_straight_to_the_maximum()
    {
        var s = new ChargeSession();
        var c = new Character(("gain_maximum_frenzy_charges_on_frenzy_charge_gained_%_chance", 10));
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "test"));
        Step(s, 0, c, new ScriptedRandom(9));
        Assert.Equal(3, s.Effective(ChargeType.Frenzy));
        s.Enqueue(new ChargeEvent.Set(ChargeType.Frenzy, 0));
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "test"));
        Step(s, 1, c, new ScriptedRandom(10));
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
    }

    [Fact]
    public void The_dev_command_sets_and_clears()
    {
        var s = new ChargeSession();
        var c = new Character();
        s.Enqueue(new ChargeEvent.Set(ChargeType.Power, 2));
        s.Enqueue(new ChargeEvent.Set(ChargeType.Endurance, 9));
        Step(s, 0, c);
        Assert.Equal(2, s.Effective(ChargeType.Power));
        Assert.Equal(3, s.Effective(ChargeType.Endurance));
        s.Enqueue(new ChargeEvent.Set(null, 0));
        Assert.True(Step(s, 1, c).CountsChanged);
        Assert.All(Charges.Types, t => Assert.Equal(0, s.Effective(t)));
    }

    [Fact]
    public void A_new_player_object_gets_every_held_buff_again()
    {
        var s = new ChargeSession();
        var c = new Character();
        s.State.Restore([new(ChargeType.Frenzy, 2, 6.5), new(ChargeType.Power, 1, 3)], 0.25);
        Assert.True(Step(s, 0.3, c).CountsChanged);
        var buffs = s.Presentation(0.3, Next);
        Assert.Equal([ChargeType.Frenzy, ChargeType.Power], buffs.Select(b => b.Type));
        Assert.Equal(6.45, buffs[0].Remaining!.Value, 6);
        Assert.All(buffs, b => Assert.Equal(0, b.Remove));
    }
}

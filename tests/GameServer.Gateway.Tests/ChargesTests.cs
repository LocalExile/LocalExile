// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
namespace GameServer.Gateway.Tests;

/// <summary>A Random whose Next(n) returns scripted values (each taken modulo n), so chance rolls are deterministic;
/// it fails the test when a roll is asked for that the script does not have.</summary>
sealed class ScriptedRandom(params int[] rolls) : Random
{
    private readonly Queue<int> queue = new(rolls);
    public int Asked { get; private set; }

    public override int Next(int maxValue)
    {
        Asked++;
        Assert.True(queue.Count > 0, "an unexpected roll");
        return queue.Dequeue() % maxValue;
    }

    public int Left => queue.Count;
}

public class ChargesTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    static readonly Charges.Limits Three = new(3, 0, Charges.BaseDurationMs);

    [Fact]
    public void A_gain_itself_discards_an_expired_stack_before_adding()
    {
        var state = new Charges.State();
        state.Gain(ChargeType.Frenzy, 3, 0, Three);
        Assert.Equal((0, 1), state.Gain(ChargeType.Frenzy, 1, 10.01, Three));
        Assert.Equal(20.01, state.ExpiresAt(ChargeType.Frenzy), 6);
    }

    [Fact]
    public void Maximum_admin_add_does_not_overflow_an_existing_stack()
    {
        var state = new Charges.State();
        state.Gain(ChargeType.Power, 2, 0, Three);
        Assert.Equal((2, 3), state.Gain(ChargeType.Power, int.MaxValue, 1, Three));
    }

    [Fact]
    public void Extreme_limits_saturate_instead_of_wrapping()
    {
        int Get(string name) => name switch
        {
            "max_power_charges" or "maximum_frenzy_power_endurance_charges" or "base_minimum_power_charges"
                or "minimum_frenzy_power_endurance_charges" or "charge_duration_+%" or "base_charge_duration_+%"
                or "power_charge_duration_+%" or "power_charge_duration_+%_final" => int.MaxValue,
            _ => 0,
        };
        Assert.Equal(new Charges.Limits(int.MaxValue, int.MaxValue, int.MaxValue), Charges.LimitsOf(ChargeType.Power, Get));
    }

    [Fact]
    public void A_character_holds_three_of_each_for_ten_seconds()
    {
        foreach (var type in Charges.Types)
            Assert.Equal(new Charges.Limits(3, 0, 10_000), Charges.LimitsOf(type, Stats()));
    }

    [Fact]
    public void Duration_takes_the_general_and_the_types_own_increases()
    {
        var get = Stats(("charge_duration_+%", 20), ("base_charge_duration_+%", 10), ("endurance_charge_duration_+%", 30),
            ("base_frenzy_charge_duration_+%", 50), ("power_charge_duration_+%", -50), ("power_charge_duration_+%_final", 100));
        Assert.Equal(16_000, Charges.LimitsOf(ChargeType.Endurance, get).DurationMs);
        Assert.Equal(18_000, Charges.LimitsOf(ChargeType.Frenzy, get).DurationMs);
        // (1 + 30 − 50 %) × 2 (more): 16 s.
        Assert.Equal(16_000, Charges.LimitsOf(ChargeType.Power, get).DurationMs);
        // Increased Frenzy Charge duration per Frenzy Charge counts the charges held after the gain.
        var perCharge = Stats(("frenzy_charge_duration_+%_per_frenzy_charge", 10));
        Assert.Equal(13_000, Charges.LimitsOf(ChargeType.Frenzy, perCharge, held: 3).DurationMs);
        Assert.Equal(10_000, Charges.LimitsOf(ChargeType.Endurance, perCharge, held: 3).DurationMs);
    }

    [Fact]
    public void Minimum_charges_are_capped_by_the_maximum()
    {
        // The wiki's example: −2 maximum and +2 minimum leave 1.
        var get = Stats(("max_frenzy_charges", -2), ("base_minimum_frenzy_charges", 2), ("minimum_frenzy_power_endurance_charges", 1),
            ("base_minimum_power_charges", 1));
        Assert.Equal(new Charges.Limits(1, 1, 10_000), Charges.LimitsOf(ChargeType.Frenzy, get));
        Assert.Equal(2, Charges.LimitsOf(ChargeType.Power, get).Minimum);
        Assert.Equal(1, Charges.LimitsOf(ChargeType.Endurance, get).Minimum);
    }

    [Fact]
    public void Gaining_adds_up_to_the_maximum_and_restarts_the_duration_even_at_the_maximum()
    {
        var state = new Charges.State();
        Assert.Equal((0, 1), state.Gain(ChargeType.Frenzy, 1, now: 0, Three));
        Assert.Equal(10, state.ExpiresAt(ChargeType.Frenzy));
        Assert.Equal((1, 3), state.Gain(ChargeType.Frenzy, 5, now: 4, Three));
        Assert.Equal(14, state.ExpiresAt(ChargeType.Frenzy));
        // At the maximum no charge is added, but the duration of all of them starts again (wiki "Charge").
        Assert.Equal((3, 3), state.Gain(ChargeType.Frenzy, 1, now: 9, Three));
        Assert.Equal(19, state.ExpiresAt(ChargeType.Frenzy));
        Assert.Equal(0, state.Held(ChargeType.Power));
        // A maximum of 0 gains nothing.
        Assert.Equal((0, 0), state.Gain(ChargeType.Power, 1, now: 9, Three with { Maximum = 0 }));
        Assert.Equal(double.NegativeInfinity, state.ExpiresAt(ChargeType.Power));
    }

    [Fact]
    public void All_charges_of_a_type_expire_together_and_the_minimum_stays()
    {
        var state = new Charges.State();
        var withMinimum = Three with { Minimum = 1 };
        state.Gain(ChargeType.Endurance, 1, 0, withMinimum);   // 1 (minimum) + 1 = 2
        state.Gain(ChargeType.Endurance, 1, 3, withMinimum);   // 3, until 13
        state.Gain(ChargeType.Power, 2, 5, Three);             // until 15
        Assert.Equal(3, state.Effective(ChargeType.Endurance, withMinimum));
        Assert.Empty(state.Expire(12.999));
        Assert.Equal([ChargeType.Endurance], state.Expire(13));
        Assert.Equal(1, state.Effective(ChargeType.Endurance, withMinimum));
        Assert.Equal(2, state.Held(ChargeType.Power));
        Assert.Equal([ChargeType.Power], state.Expire(15));
        Assert.Empty(state.Expire(100));
    }

    [Fact]
    public void A_lower_maximum_removes_the_charges_above_it()
    {
        var state = new Charges.State();
        state.Gain(ChargeType.Power, 5, 0, Three with { Maximum = 5 });
        Assert.True(state.Clamp(ChargeType.Power, Three));
        Assert.Equal(3, state.Held(ChargeType.Power));
        Assert.False(state.Clamp(ChargeType.Power, Three));
        Assert.True(state.Clamp(ChargeType.Power, Three with { Maximum = 0 }));
        Assert.Equal(0, state.Remaining(ChargeType.Power, 1));
    }

    [Fact]
    public void An_area_change_carries_the_charges_with_the_time_they_had_left()
    {
        var old = new Charges.State();
        old.Gain(ChargeType.Frenzy, 2, 100, Three);   // until 110
        old.Gain(ChargeType.Power, 1, 95, Three);     // until 105
        var carried = old.Carry(103);
        Assert.Equal([new(ChargeType.Frenzy, 2, 7), new(ChargeType.Power, 1, 2)], carried);
        // The new area's clock starts near 0.
        var next = new Charges.State();
        next.Restore(carried, 0.5);
        Assert.Equal(2, next.Held(ChargeType.Frenzy));
        Assert.Equal(7.5, next.ExpiresAt(ChargeType.Frenzy), 6);
        Assert.Equal(2.5, next.ExpiresAt(ChargeType.Power), 6);
        Assert.Equal(0, next.Held(ChargeType.Endurance));
        // Charges whose time ran out are not carried.
        Assert.Empty(old.Carry(110));
    }

    [Fact]
    public void Death_takes_every_charge()
    {
        var state = new Charges.State();
        state.Gain(ChargeType.Endurance, 3, 0, Three);
        Assert.True(state.Clear());
        Assert.All(Charges.Types, t => Assert.Equal(0, state.Held(t)));
        Assert.False(state.Clear());
    }
}

namespace GameServer.Gateway.Tests;

public class ChargeExpiryClockTests
{
    [Fact]
    public void Generic_received_rate_preserves_all_charge_budgets_across_rate_changes_and_area_carry()
    {
        var state = new Charges.State();
        var limits = new Charges.Limits(3, 0, 10000);
        state.SetExpiryRate(0, .88);
        foreach (var type in Charges.Types) state.Gain(type, 1, 0, limits);
        Assert.Equal(10 / .88, state.ExpiresAt(ChargeType.Endurance), 8);
        state.SetExpiryRate(2, 1);
        Assert.Equal(8.24, state.RemainingBase(ChargeType.Endurance, 2), 8);
        Assert.Equal(10.24, state.ExpiresAt(ChargeType.Endurance), 8);
        state.SetExpiryRate(3, .88);
        Assert.Equal(7.24, state.RemainingBase(ChargeType.Endurance, 3), 8);
        var carry = state.Carry(3);
        var arrived = new Charges.State(); arrived.Restore(carry, 100);
        Assert.Equal(.88, arrived.ExpiryRate);
        arrived.SetExpiryRate(100, 1); // The received curse does not travel with this fixture.
        Assert.Equal(107.24, arrived.ExpiresAt(ChargeType.Endurance), 8);
        Assert.Empty(arrived.Expire(107.23));
        Assert.Equal(3, arrived.Expire(107.24).Count);
    }

    [Fact]
    public void Refresh_uses_base_duration_once_and_expired_charges_cannot_be_revived_by_rate_refund()
    {
        var state = new Charges.State(); var limits = new Charges.Limits(3, 0, 10000);
        state.SetExpiryRate(0, .5); state.Gain(ChargeType.Power, 1, 0, limits);
        state.Gain(ChargeType.Power, 1, 2, limits);
        Assert.Equal(22, state.ExpiresAt(ChargeType.Power));
        state.SetExpiryRate(23, 1);
        Assert.Equal(0, state.RemainingBase(ChargeType.Power, 23));
        Assert.Single(state.Expire(23));
        Assert.Equal(0, state.Held(ChargeType.Power));
    }

    [Fact]
    public void Charge_category_ignores_Harbinger_buff_and_debuff_sources_but_consumes_generic_curse_expiry()
    {
        int Get(string name) => name switch
        {
            "buff_time_passed_+%_only_buff_category" => -30,
            "debuff_time_passed_+%" => 100,
            "buff_time_passed_+%_other_than_temporal_chains" => -12,
            _ => 0,
        };
        Assert.Equal(.88, EffectClock.RecipientRate(3, Get));
    }
}

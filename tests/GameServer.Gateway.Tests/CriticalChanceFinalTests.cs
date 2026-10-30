namespace GameServer.Gateway.Tests;

public class CriticalChanceFinalTests
{
    static Func<string, int> Raw(params (string Name, int Value)[] stats) => name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Theory]
    [InlineData("support_controlled_destruction_critical_strike_chance_+%_final")]
    [InlineData("support_multiple_projectiles_critical_strike_chance_+%_final")]
    [InlineData("unique_critical_strike_chance_+%_final")]
    [InlineData("active_skill_critical_strike_chance_+%_final")]
    public void Actor_and_own_values_share_one_raw_bucket_and_final_aliases_do_not_feed_back(string stat)
    {
        var actor = Raw((stat, -80), ("combined_critical_strike_chance_+%_final", -100),
            ("main_hand_critical_strike_chance", 9999), ("spell_critical_strike_chance", 9999));
        var own = Raw((stat, 30));
        Assert.Equal(250, AddedBaseCriticalStrike.Chance(500, 0, false, actor, own));
        Assert.Equal(250, AddedBaseCriticalStrike.Chance(500, 0, true, actor, own));
        Assert.True(AttackBaseCriticalStrike.IsInput(stat));
    }

    [Theory]
    [InlineData(501, 0, -50, 251)]
    [InlineData(500, 3, -50, 258)]
    [InlineData(500, 0, -100, 0)]
    [InlineData(500, 0, -200, 0)]
    [InlineData(500, -101, -200, 0)]
    [InlineData(-500, -200, 100, 0)]
    public void One_final_half_up_round_and_nonnegative_factors_preserve_boundaries(int basis, long increased, int final, int expected) =>
        Assert.Equal(expected, AddedBaseCriticalStrike.Chance(basis, increased, false,
            Raw(("active_skill_critical_strike_chance_+%_final", final))));

    [Fact]
    public void Extreme_sources_do_not_overflow_or_lose_signed_cancellation()
    {
        Assert.Equal(10000, AddedBaseCriticalStrike.Chance(int.MaxValue, long.MaxValue, false, _ => int.MaxValue, _ => int.MaxValue));
        Assert.Equal(0, AddedBaseCriticalStrike.Chance(int.MaxValue, long.MinValue, true, _ => int.MaxValue, _ => int.MaxValue));
        Assert.Equal(500, AddedBaseCriticalStrike.Chance(500, 0, false,
            Raw((CriticalChanceFinal.ControlledDestruction, int.MaxValue)), Raw((CriticalChanceFinal.ControlledDestruction, -int.MaxValue))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prohibition_still_wins_over_more_chance(bool spell)
    {
        var actor = Raw(("global_cannot_crit", 1));
        var own = Raw(("active_skill_critical_strike_chance_+%_final", int.MaxValue));
        Assert.Equal(0, spell ? SpellCriticalStrike.Chance(500, actor, own)
            : AttackBaseCriticalStrike.ChanceOfBase(500, 0, actor, own));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void More_less_factors_apply_after_BASE_and_INC_before_cap_and_rounding(bool spell)
    {
        var player = Raw(("unique_critical_strike_chance_+%_final", -40));
        var own = Raw(("support_controlled_destruction_critical_strike_chance_+%_final", -80),
            ("active_skill_critical_strike_chance_+%_final", 100));
        int actual = spell ? SpellCriticalStrike.Chance(5000, player, own, 300)
            : AttackBaseCriticalStrike.ChanceOfBase(5000, 300, player, own);
        Assert.Equal(4800, actual); // 50% * 4 * .6 * .2 * 2, not a prematurely capped 24%
    }
}

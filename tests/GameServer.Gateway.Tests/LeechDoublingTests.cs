namespace GameServer.Gateway.Tests;

public class LeechDoublingTests
{
    const string Total = "total_recovery_per_minute_from_life_leech_is_doubled";
    const string Legacy = "life_leech_speed_is_doubled";
    const string Maximum = "maximum_life_leech_rate_%_per_minute_is_doubled";

    [Theory]
    [InlineData(0, 0, 60)]
    [InlineData(1, 0, 120)]
    [InlineData(0, 1, 120)]
    [InlineData(2, 0, 120)]
    [InlineData(1, 1, 120)]
    public void Total_doubling_is_one_boolean_multiplier_after_increases_without_changing_duration_or_instant_amount(
        int total, int legacy, int expected)
    {
        Dictionary<string, int> raw = new() { [Total] = total, [Legacy] = legacy, ["life_leech_speed_+%"] = 100,
            ["life_recovery_rate_+%"] = 50, ["leech_%_is_instant"] = 25 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        Assert.Equal((25d, 25d, 25d), state.Hit(0, (100, 100, 100), false, false, false));
        Assert.Equal(((double)expected, 20d, 20d), state.TakeRecovery(1));
        Assert.Equal((expected * 2.75, 55d, 55d), state.TakeRecovery(3.75));
        Assert.Equal(default, state.Active(3.75));
    }

    [Theory]
    [InlineData(false, false, 120)]
    [InlineData(true, false, 200)]
    [InlineData(false, true, 120)]
    [InlineData(true, true, 240)]
    public void Total_recovery_and_maximum_recovery_doubling_are_separate_operators(bool total, bool maximum, int expected)
    {
        Dictionary<string, int> raw = new() { [Total] = total ? 1 : 0, [Maximum] = maximum ? 1 : 0 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var limits = LeechLimits.Resolve(Get, 1000, 1000, 1000);
        var rows = LeechLimits.Compute(Get, 1000, 1000, 1000).ToDictionary(r => r.Stat, r => r.Value);
        Assert.Equal(maximum ? 400 : 200, limits.Resources[0].MaximumPerSecond);
        Assert.Equal(maximum ? 24000 : 12000, rows["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(100, limits.Resources[0].MaximumPerInstance);
        var state = new LeechSession(); state.Configure(0, limits, Get, _ => null);
        for (int i = 0; i < 6; i++) state.Hit(0, (100, 100, 100), false, false, false);
        Assert.Equal(((double)expected, 120d, 100d), state.TakeRecovery(1));
        Assert.Equal((expected * 4d, 480d, 400d), state.TakeRecovery(5));
        Assert.Equal(default, state.Active(5));
    }

    [Fact]
    public void Removing_doubling_settles_old_recovery_and_preserves_instance_expiry()
    {
        Dictionary<string, int> raw = new() { [Total] = 1 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        state.Hit(0, (100, 0, 0), false, false, false);
        raw.Clear();
        state.Configure(2, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        Assert.Equal(140, state.TakeRecovery(5).Life);
        Assert.False(state.Active(5).Life);
    }

    [Fact]
    public void Life_doubling_does_not_modify_converted_or_native_ES_recovery()
    {
        Dictionary<string, int> raw = new() { [Total] = 1, [Maximum] = 1, ["keystone_ghost_reaver"] = 1 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        state.Hit(0, (100, 0, 100), false, false, false);
        Assert.Equal((0d, 0d, 40d), state.TakeRecovery(1));
        Assert.Equal((0d, 0d, 160d), state.TakeRecovery(5));
    }

    [Theory]
    [InlineData("maximum_life_leech_rate_%_per_minute_is_doubled", 0, 200)]
    [InlineData("virtual_maximum_life_leech_rate_permyriad_per_minute", 0, 0)]
    [InlineData("virtual_maximum_life_leech_rate_permyriad_per_minute", 90000, 150)]
    public void Maximum_doubling_preserves_raw_and_final_replacement_precedence(string name, int value, int expected)
    {
        int Get(string n) => n == Maximum ? 1 : 0;
        var limits = LeechLimits.Resolve(Get, 1000, 1000, 1000, n => n == name ? value : null);
        Assert.Equal(expected, limits.Resources[0].MaximumPerSecond);
    }
}

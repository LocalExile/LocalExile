using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class FlaskDurationCompositionTests
{
    static Item Flask(byte quality = 0) => new(1, ["Base", "Charges", "Flask"], 2) { Quality = quality };
    static readonly UtilityFlasks.Utility Utility = new(0, 120, "test", [], 6, 30, 60);
    static readonly Flasks.Flask Life = new(70, 0, 3, 7, 21), Mana = new(0, 50, 3, 6, 24);
    static Func<string, int> Stats(params (string Name, int Value)[] values)
    {
        var raw = values.ToDictionary(v => v.Name, v => v.Value);
        return name => raw.GetValueOrDefault(name);
    }
    static Dictionary<string, int> Local(params (string Name, int Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);

    [Theory]
    [InlineData(0, 0, 6d)]
    [InlineData(30, 0, 7.8d)]
    [InlineData(0, 20, 7.2d)]
    [InlineData(30, 20, 9.36d)]
    public void Item_local_and_global_utility_duration_multiply_as_separate_factors(int local, int global, double expected)
    {
        var result = UtilityFlasks.For(Utility, Flask(), Local(("local_flask_duration_+%", local)), Stats(("flask_duration_+%", global)));
        Assert.Equal(expected, result.Seconds, 9);
    }

    [Fact]
    public void Quality_and_local_more_keep_their_own_factors_after_local_and_global_duration()
    {
        Assert.Equal(11.232, UtilityFlasks.For(Utility, Flask(20), Local(("local_flask_duration_+%", 30)),
            Stats(("flask_duration_+%", 20))).Seconds, 9);
        Assert.Equal(6.7392, UtilityFlasks.For(Utility, Flask(20), Local(("local_flask_duration_+%", 30),
            ("local_flask_duration_+%_final", -40)), Stats(("flask_duration_+%", 20))).Seconds, 9);
    }

    [Theory]
    [InlineData(10, 130, 13.8, 6.9, 161, 115)]
    [InlineData(20, 110, 12.6, 6.3, 147, 105)]
    [InlineData(100, -50, 3.0, 1.5, 35, 25)]
    public void Full_Nevalius_duration_pair_scales_utility_and_recovery_from_one_global_coefficient(
        int level, int coefficient, double utilitySeconds, double recoverySeconds, double life, double mana)
    {
        var raw = Stats(("flask_duration_+%", 150), ("flask_duration_+%_per_level", -2),
            ("virtual_flask_duration_+%", 777));
        Assert.Equal(coefficient, PanelStats.FlaskDurationIncrease(raw, level));
        Assert.Equal(utilitySeconds, UtilityFlasks.For(Utility, Flask(), Local(), raw, level).Seconds, 9);
        var lifeUse = FlaskRecovery.For(Life, Flask(), Local(), raw, 100, 1000, level);
        var manaUse = FlaskRecovery.For(Mana, Flask(), Local(), raw, 100, 1000, level);
        Assert.Equal(recoverySeconds, lifeUse.Seconds, 9);
        Assert.Equal(recoverySeconds, manaUse.Seconds, 9);
        Assert.Equal(life, lifeUse.Life, 9);
        Assert.Equal(mana, manaUse.Mana, 9);
        Assert.Equal(0, lifeUse.InstantLife);
    }

    [Fact]
    public void Global_duration_scales_only_recovery_over_time_and_not_the_instant_part()
    {
        var result = FlaskRecovery.For(Life, Flask(), Local(("local_flask_recovery_amount_%_to_recover_instantly", 50)),
            Stats(("flask_duration_+%", 150), ("flask_duration_+%_per_level", -2)), 100, 1000, 20);
        Assert.Equal(35, result.InstantLife);
        Assert.Equal(73.5, result.Life, 9);
        Assert.Equal(6.3, result.Seconds, 9);
    }

    [Fact]
    public void Recovery_local_duration_and_resource_rate_remain_separate_from_the_shared_global_factor()
    {
        var raw = Stats(("flask_duration_+%", 150), ("flask_duration_+%_per_level", -2), ("flask_life_recovery_rate_+%", 100));
        var result = FlaskRecovery.For(Life, Flask(), Local(("local_flask_duration_+%", 30)), raw, 100, 1000, 20);
        Assert.Equal(147, result.Life, 9);
        Assert.Equal(4.095, result.Seconds, 9); //3*1.3*2.1/2; local duration does not add another global amount factor.
    }

    [Fact]
    public void Runtime_uses_wide_global_coefficients_without_reading_the_display_or_an_invented_level_stat()
    {
        var raw = Stats(("flask_duration_+%", int.MaxValue), ("flask_duration_+%_per_level", int.MaxValue),
            ("virtual_flask_duration_+%", -100), ("level", 1));
        Assert.Equal(216895848347L, PanelStats.FlaskDurationIncrease(raw, 100));
        Assert.Equal(4294967294L, PanelStats.FlaskDurationIncrease(raw)); //Existing API fallback is actual level1.
        var result = UtilityFlasks.For(Utility, Flask(), Local(("local_flask_duration_+%", int.MaxValue)), raw, 100);
        Assert.True(double.IsFinite(result.Seconds));
        Assert.True(result.Seconds > int.MaxValue);
    }
}

namespace GameServer.Gateway.Tests;

public class LeechResolvedLimitsTests
{
    [Fact]
    public void Small_pools_keep_fractional_combat_caps_even_when_wire_amounts_truncate_to_zero()
    {
        var actual = LeechLimits.Resolve(_ => 0, 1, 2, 3);
        Assert.Equal(new[] { .1, .2, .3 }, actual.Resources.Select(x => x.MaximumPerInstance));
        Assert.Equal(new[] { .2, .4, .3 }, actual.Resources.Select(x => x.MaximumPerSecond));
        var display = LeechLimits.Compute(_ => 0, 1, 2, 3).ToDictionary(x => x.Stat, x => x.Value);
        foreach (var row in actual.Resources) Assert.Equal(0, display[$"maximum_{row.Pool}_leech_amount_per_leech"]);
    }

    [Fact]
    public void Resolved_caps_share_prohibitions_and_explicit_zero_replacements_with_the_panel()
    {
        var sources = new Dictionary<string, int> { ["cannot_leech_when_on_low_life"] = 1 };
        var actual = LeechLimits.Resolve(sources.GetValueOrDefault, 1000, 1000, 1000, onLowLife: true);
        Assert.True(actual.Resources[0].CannotLeech);
        Assert.True(actual.Resources[1].CannotLeech);
        Assert.False(actual.Resources[2].CannotLeech);
        Assert.Equal(new[] { 0.0, 0, 100 }, actual.Resources.Select(x => x.MaximumPerInstance));
        actual = LeechLimits.Resolve(_ => 0, 1000, 1000, 1000,
            name => name == "maximum_mana_leech_rate_%_per_minute" ? 0 : null);
        Assert.Equal(0, actual.Resources[1].MaximumPerSecond);
        Assert.Equal(100, actual.Resources[1].MaximumPerInstance);
    }

    [Fact]
    public void Ggg_worked_example_resolves_amount_and_rate_caps_independently()
    {
        var sources = new Dictionary<string, int>
        {
            ["maximum_life_leech_amount_per_leech_+%"] = 50,
            ["maximum_life_leech_rate_+%"] = 15,
            ["life_leech_speed_+%"] = 100,
            ["life_recovery_rate_+%"] = 50,
        };
        var resource = LeechLimits.Resolve(sources.GetValueOrDefault, 1000, 1000, 1000).Resources[0];
        Assert.Equal(150, resource.MaximumPerInstance);
        Assert.Equal(230, resource.MaximumPerSecond);
    }
}

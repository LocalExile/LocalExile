namespace GameServer.Gateway.Tests;

public class ConvertedInstantLeechTests
{
    [Theory]
    [InlineData("keystone_ghost_reaver")]
    [InlineData("leech_energy_shield_instead_of_life")]
    public void Life_instant_percentage_follows_the_converted_source_and_common_percentage_is_counted_once(string conversion)
    {
        Dictionary<string, int> raw = new() { [conversion] = 1, ["leech_%_is_instant"] = 10,
            ["life_leech_%_is_instant"] = 30, ["life_leech_speed_+%"] = 500 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1, 1000, 1000), Get, _ => null);
        Assert.Equal((0d, 10d, 50d), state.Hit(0, (1000, 100, 1000), true, false, false));
        Assert.Equal(2, state.EnergyShield.Count);
        Assert.Equal((0d, 90d, 150d), state.TakeRecovery(10));
    }

    [Theory]
    [InlineData(0, 15, 10)]
    [InlineData(1, 55, 40)]
    [InlineData(2, 65, 40)]
    public void Claw_sources_preserve_per_claw_Life_and_once_while_wielding_all_resource_scopes(int claws, int life, int other)
    {
        Dictionary<string, int> raw = new() { ["leech_%_is_instant"] = 10, ["life_leech_%_is_instant"] = 5,
            ["life_leech_%_is_instant_per_equipped_claw"] = 10, ["leech_%_is_instant_while_wielding_claw"] = 30 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null, claws);
        Assert.Equal(((double)life, (double)other, (double)other), state.Hit(0, (100, 100, 100), false, false, false));
        Assert.Equal((100d - life, 100d - other, 100d - other), state.TakeRecovery(10));
    }

    [Theory]
    [InlineData("virtual_life_leech_%_is_instant", 0, 0, 10)]
    [InlineData("virtual_life_leech_%_is_instant", 60, 0, 70)]
    [InlineData("virtual_energy_shield_leech_%_is_instant", 0, 0, 0)]
    [InlineData("virtual_energy_shield_leech_%_is_instant", 60, 0, 120)]
    [InlineData("virtual_leech_energy_shield_instead_of_life", 0, 40, 10)]
    public void Explicit_replacements_override_their_final_scope_including_zero(string name, int value, int life, int es)
    {
        Dictionary<string, int> raw = new() { ["keystone_ghost_reaver"] = 1, ["leech_%_is_instant"] = 10,
            ["life_leech_%_is_instant_per_equipped_claw"] = 30 };
        int Get(string n) => raw.GetValueOrDefault(n);
        int? Replacement(string n) => n == name ? value : null;
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, Replacement, 1);
        Assert.Equal(((double)life, 10d, (double)es), state.Hit(0, (100, 100, 100), false, false, false));
    }

    [Theory]
    [InlineData(int.MaxValue, 200)]
    [InlineData(int.MinValue, 0)]
    public void Extreme_sources_clamp_without_integer_overflow_or_phantom_instances(int percent, int expected)
    {
        Dictionary<string, int> raw = new() { ["keystone_ghost_reaver"] = 1, ["leech_%_is_instant"] = percent,
            ["life_leech_%_is_instant_per_equipped_claw"] = percent };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null, 2);
        Assert.Equal((0d, 0d, (double)expected), state.Hit(0, (100, 0, 100), false, false, false));
        Assert.Equal(expected == 0 ? 2 : 0, state.EnergyShield.Count);
    }

    [Fact]
    public void Changing_instant_percentage_does_not_retroactively_split_existing_instances()
    {
        Dictionary<string, int> raw = new() { ["keystone_ghost_reaver"] = 1, ["life_leech_%_is_instant"] = 20 };
        int Get(string n) => raw.GetValueOrDefault(n);
        var state = new LeechSession();
        state.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        Assert.Equal((0d, 0d, 20d), state.Hit(0, (100, 0, 0), true, false, false));
        raw["life_leech_%_is_instant"] = 100;
        state.Configure(1, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        Assert.Equal((0d, 0d, 100d), state.Hit(1, (100, 0, 0), true, false, false));
        Assert.Equal((0d, 0d, 80d), state.TakeRecovery(10));
    }
}

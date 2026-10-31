using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class LevelScaledPanelStatsTests
{
    static Dictionary<string, int> Compute(int level, params (string Name, int Value)[] values)
    {
        var raw = values.ToDictionary(v => v.Name, v => v.Value);
        return PanelStats.Compute(n => raw.GetValueOrDefault(n), playerLevel: level).ToDictionary(v => v.Stat, v => v.Value);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(10, 15)]
    [InlineData(20, 25)]
    [InlineData(100, 105)]
    public void All_three_on_kill_amounts_include_the_actual_player_level(int level, int expected)
    {
        var raw = new Dictionary<string, int>();
        foreach (string pool in new[] { "life", "mana", "energy_shield" })
        {
            raw[$"base_{pool}_gained_on_enemy_death"] = 3;
            raw[$"{pool}_gained_on_enemy_death"] = 2;
            raw[$"{pool}_gained_on_enemy_death_per_level"] = 1;
        }
        var before = raw.ToArray();
        var first = PanelStats.Compute(n => raw.GetValueOrDefault(n), playerLevel: level).ToDictionary(v => v.Stat, v => v.Value);
        var again = PanelStats.Compute(n => raw.GetValueOrDefault(n), playerLevel: level).ToDictionary(v => v.Stat, v => v.Value);
        foreach (string pool in new[] { "life", "mana", "energy_shield" })
            Assert.Equal(expected, first[$"{pool}_gained_on_enemy_death"]);
        Assert.Equal(first.OrderBy(v => v.Key), again.OrderBy(v => v.Key));
        Assert.Equal(before, raw.ToArray());
    }

    [Fact]
    public void Player_level_is_explicit_and_old_callers_keep_the_level_one_fallback()
    {
        int Get(string name) => name switch { "life_gained_on_enemy_death_per_level" => 1, "level" => 999, _ => 0 };
        Assert.Equal(1, PanelStats.Compute(Get).Single(v => v.Stat == "life_gained_on_enemy_death").Value);
        Assert.Equal(20, PanelStats.Compute(Get, playerLevel: 20).Single(v => v.Stat == "life_gained_on_enemy_death").Value);
        Assert.Equal(1, Compute(0, ("life_gained_on_enemy_death_per_level", 1))["life_gained_on_enemy_death"]);
        Assert.Equal(100, Compute(int.MaxValue, ("life_gained_on_enemy_death_per_level", 1))["life_gained_on_enemy_death"]);
    }

    [Theory]
    [InlineData(10, 130)]
    [InlineData(20, 110)]
    [InlineData(100, -50)]
    public void Nevalius_relevant_duration_pair_uses_player_level_and_keeps_signed_panel_values(int level, int expected)
    {
        var result = Compute(level, ("flask_duration_+%", 150), ("flask_duration_+%_per_level", -2));
        Assert.Equal(expected, result["virtual_flask_duration_+%"]);
        Assert.Equal(expected + 7, Compute(level, ("flask_duration_+%", 150),
            ("flask_duration_+%_per_level", -2), ("virtual_flask_duration_+%", 7))["virtual_flask_duration_+%"]);
    }

    [Fact]
    public void Signed_per_level_sources_cancel_before_final_native_integer_saturation()
    {
        var result = Compute(100, ("life_gained_on_enemy_death_per_level", int.MaxValue),
            ("mana_gained_on_enemy_death_per_level", int.MinValue),
            ("energy_shield_gained_on_enemy_death_per_level", -1), ("base_energy_shield_gained_on_enemy_death", 100));
        Assert.Equal(int.MaxValue, result["life_gained_on_enemy_death"]);
        Assert.Equal(int.MinValue, result["mana_gained_on_enemy_death"]);
        Assert.False(result.ContainsKey("energy_shield_gained_on_enemy_death"));
        Assert.Equal(-1, Compute(1, ("life_gained_on_enemy_death_per_level", int.MaxValue),
            ("base_life_gained_on_enemy_death", int.MinValue))["life_gained_on_enemy_death"]);
        // Coefficient publication has no runtime-duration clamp or percentage floor.
        Assert.Equal(-200, Compute(100, ("flask_duration_+%_per_level", -2))["virtual_flask_duration_+%"]);
    }

    [Fact]
    public void Block_recovery_uses_base_and_direct_stun_recovery_once_in_the_same_raw_snapshot()
    {
        var result = Compute(20, ("base_stun_recovery_+%", 13), ("stun_recovery_+%", 7),
            ("block_recovery_+%", 60), ("virtual_block_recovery_+%", 9));
        Assert.Equal(20, result["stun_recovery_+%"]);
        Assert.Equal(89, result["virtual_block_recovery_+%"]);
    }

    [Theory]
    [InlineData(0, 72)]
    [InlineData(1, 82)]
    [InlineData(3, 102)]
    public void Already_expanded_stun_recovery_per_frenzy_charge_also_counts_for_block_once(int held, int expected)
    {
        var raw = new Dictionary<string, int> { ["base_stun_recovery_+%"] = 12,
            ["block_recovery_+%"] = 60, ["stun_recovery_+%_per_frenzy_charge"] = 10 };
        var expanded = ChargeEffects.Expand(n => raw.GetValueOrDefault(n), type => type == ChargeType.Frenzy ? held : 0);
        var result = PanelStats.Compute(n => raw.GetValueOrDefault(n) + expanded.GetValueOrDefault(n)).ToDictionary(v => v.Stat, v => v.Value);
        Assert.Equal(expected, result["virtual_block_recovery_+%"]);
        Assert.Equal(expected - 60, result["stun_recovery_+%"]);
    }

    [Fact]
    public void Combined_recovery_signed_sources_and_large_values_preserve_existing_publication_bounds()
    {
        Assert.Equal(-140, Compute(1, ("base_stun_recovery_+%", -200), ("block_recovery_+%", 60))["virtual_block_recovery_+%"]);
        Assert.Equal(int.MaxValue, Compute(1, ("base_stun_recovery_+%", int.MaxValue),
            ("stun_recovery_+%", int.MaxValue), ("block_recovery_+%", int.MaxValue))["virtual_block_recovery_+%"]);
    }
}

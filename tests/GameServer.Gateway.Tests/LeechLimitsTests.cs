namespace GameServer.Gateway.Tests;

public sealed class LeechLimitsTests
{
    static Dictionary<string, int> Calculate(uint life = 1000, uint mana = 500, uint es = 200,
        params (string Stat, int Value)[] inputs)
    {
        var stats = inputs.ToDictionary(s => s.Stat, s => s.Value);
        return LeechLimits.Compute(n => stats.GetValueOrDefault(n), life, mana, es).ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Character_defaults_publish_all_six_limits_and_the_native_units_without_a_leech_source()
    {
        var rows = Calculate();
        foreach (string pool in new[] { "life", "mana", "energy_shield" })
        {
            Assert.Equal(1000, rows[$"virtual_maximum_{pool}_leech_amount_per_leech_permyriad_max_{pool}"]);
            Assert.Equal(0, rows[$"cannot_leech_{pool}"]);
        }
        Assert.Equal(100, rows["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(50, rows["maximum_mana_leech_amount_per_leech"]);
        Assert.Equal(20, rows["maximum_energy_shield_leech_amount_per_leech"]);
        Assert.Equal(120000, rows["virtual_maximum_life_leech_rate_permyriad_per_minute"]);
        Assert.Equal(120000, rows["virtual_maximum_mana_leech_rate_permyriad_per_minute"]);
        Assert.Equal(60000, rows["virtual_maximum_energy_shield_leech_rate_permyriad_per_minute"]);
        Assert.Equal(12000, rows["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(6000, rows["maximum_total_recovery_per_minute_from_mana_leech"]);
        Assert.Equal(1200, rows["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
    }

    [Fact]
    public void Typed_and_generic_rate_increases_add_without_affecting_instance_amount_or_recovery_speed()
    {
        var rows = Calculate(inputs: [("maximum_life_leech_rate_+%", 40), ("maximum_leech_rate_+%", 20),
            ("maximum_mana_leech_rate_+%", -10), ("life_leech_speed_+%", 500), ("life_mana_es_leech_speed_+%", 100)]);
        Assert.Equal(19200, rows["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(6600, rows["maximum_total_recovery_per_minute_from_mana_leech"]);
        Assert.Equal(1440, rows["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
        Assert.Equal(100, rows["maximum_life_leech_amount_per_leech"]);
    }

    [Fact]
    public void Instance_increases_and_the_native_ES_double_flag_retain_their_own_scope()
    {
        var rows = Calculate(inputs: [("maximum_life_leech_amount_per_leech_+%", 100),
            ("maximum_mana_leech_amount_per_leech_+%", 50), ("maximum_energy_shield_leech_amount_per_leech_is_doubled", 1)]);
        Assert.Equal(200, rows["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(75, rows["maximum_mana_leech_amount_per_leech"]);
        Assert.Equal(40, rows["maximum_energy_shield_leech_amount_per_leech"]);
        Assert.Equal(12000, rows["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(6000, rows["maximum_total_recovery_per_minute_from_mana_leech"]);
        Assert.Equal(1200, rows["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
    }

    [Fact]
    public void Reduced_maximum_leech_from_the_map_adds_to_increases_instead_of_becoming_less_recovery()
    {
        // Native MapUberModLessPlayerLeech's description is "reduced", despite the modifier Id's "Less".
        var rows = Calculate(inputs: [("maximum_life_leech_rate_+%", 40), ("maximum_leech_rate_+%", 20),
            ("map_player_maximum_leech_rate_+%", -50)]);
        Assert.Equal(13200, rows["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(4200, rows["maximum_total_recovery_per_minute_from_mana_leech"]);
        Assert.Equal(840, rows["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
    }

    [Fact]
    public void Deferred_Wicked_Ward_virtual_does_not_imply_its_source_mechanics_are_implemented()
    {
        var rows = Calculate(inputs: [("wicked_ward_keystone_maximum_energy_shield_leech_rate_+%_final", -50)]);
        Assert.Equal(12000, rows["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(1200, rows["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
    }

    [Theory]
    [InlineData("base_cannot_leech")]
    [InlineData("cannot_leech")]
    [InlineData("map_players_cannot_leech")]
    public void General_prohibitions_clear_every_limit_and_publish_each_pool_gate(string flag)
    {
        var rows = Calculate(inputs: [(flag, 1)]);
        Assert.Equal(1, rows["cannot_leech"]);
        Assert.All(rows.Where(r => r.Key.StartsWith("maximum_") || r.Key.StartsWith("virtual_maximum_")), r => Assert.Equal(0, r.Value));
        foreach (string pool in new[] { "life", "mana", "energy_shield" }) Assert.Equal(1, rows[$"cannot_leech_{pool}"]);
    }

    [Theory]
    [InlineData("life", "base_cannot_leech_life")]
    [InlineData("life", "cannot_leech_life")]
    [InlineData("mana", "base_cannot_leech_mana")]
    [InlineData("mana", "cannot_leech_mana")]
    [InlineData("mana", "cannot_leech_or_regenerate_mana")]
    [InlineData("energy_shield", "base_cannot_leech_energy_shield")]
    [InlineData("energy_shield", "cannot_leech_energy_shield")]
    public void A_pool_prohibition_does_not_suppress_other_pools(string blocked, string flag)
    {
        var rows = Calculate(inputs: [(flag, 1)]);
        foreach (string pool in new[] { "life", "mana", "energy_shield" })
        {
            Assert.Equal(pool == blocked ? 1 : 0, rows[$"cannot_leech_{pool}"]);
            Assert.Equal(pool == blocked, rows[$"maximum_{pool}_leech_amount_per_leech"] == 0);
        }
    }

    [Fact]
    public void Low_life_prohibition_blocks_Life_and_Mana_while_leaving_ES_leech_available()
    {
        int Get(string name) => name == "cannot_leech_when_on_low_life" ? 1 : 0;
        var above = LeechLimits.Compute(Get, 1000, 500, 200).ToDictionary(s => s.Stat, s => s.Value);
        var low = LeechLimits.Compute(Get, 1000, 500, 200, onLowLife: true).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(0, above["cannot_leech"]);
        Assert.Equal(100, above["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(50, above["maximum_mana_leech_amount_per_leech"]);
        Assert.Equal(0, low["cannot_leech"]);
        Assert.Equal(1, low["cannot_leech_life"]);
        Assert.Equal(1, low["cannot_leech_mana"]);
        Assert.Equal(0, low["cannot_leech_energy_shield"]);
        Assert.Equal(0, low["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(0, low["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(0, low["maximum_mana_leech_amount_per_leech"]);
        Assert.Equal(0, low["maximum_total_recovery_per_minute_from_mana_leech"]);
        Assert.Equal(20, low["maximum_energy_shield_leech_amount_per_leech"]);
        Assert.Equal(1200, low["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
        Assert.Equal(above["maximum_energy_shield_leech_amount_per_leech"], low["maximum_energy_shield_leech_amount_per_leech"]);
    }

    [Fact]
    public void Explicit_zero_default_and_virtual_replacements_are_distinct_from_absent_sources()
    {
        var overrides = new Dictionary<string, int>
        {
            ["maximum_life_leech_amount_per_leech_%_max_life"] = 0,
            ["maximum_mana_leech_rate_%_per_minute"] = 0,
            ["virtual_maximum_energy_shield_leech_amount_per_leech_permyriad_max_energy_shield"] = 0,
            ["virtual_maximum_life_leech_rate_permyriad_per_minute"] = 30000,
        };
        var rows = LeechLimits.Compute(_ => 0, 1000, 500, 200,
            n => overrides.TryGetValue(n, out int value) ? value : null).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(0, rows["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(0, rows["maximum_total_recovery_per_minute_from_mana_leech"]);
        Assert.Equal(0, rows["maximum_energy_shield_leech_amount_per_leech"]);
        Assert.Equal(3000, rows["maximum_total_recovery_per_minute_from_life_leech"]);
    }

    [Fact]
    public void Signed_increases_cancel_before_clamping_and_extreme_values_cannot_overflow()
    {
        var cancelled = Calculate(inputs: [("maximum_life_leech_rate_+%", int.MaxValue), ("maximum_leech_rate_+%", int.MinValue)]);
        Assert.Equal(11880, cancelled["maximum_total_recovery_per_minute_from_life_leech"]);
        var extreme = Calculate(uint.MaxValue, uint.MaxValue, uint.MaxValue,
            ("maximum_life_leech_rate_%_per_minute", int.MaxValue), ("maximum_leech_rate_+%", int.MaxValue),
            ("maximum_life_leech_rate_+%", int.MaxValue), ("maximum_life_leech_amount_per_leech_%_max_life", int.MaxValue),
            ("maximum_life_leech_amount_per_leech_+%", int.MaxValue));
        Assert.Equal(int.MaxValue, extreme["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(int.MaxValue, extreme["maximum_total_recovery_per_minute_from_life_leech"]);
        var negative = Calculate(inputs: [("maximum_leech_rate_+%", int.MinValue), ("maximum_life_leech_amount_per_leech_+%", int.MinValue)]);
        Assert.Equal(0, negative["maximum_total_recovery_per_minute_from_life_leech"]);
        Assert.Equal(0, negative["maximum_life_leech_amount_per_leech"]);
    }

    [Fact]
    public void Fractional_wire_endpoints_truncate_once_and_zero_ES_does_not_invent_capacity()
    {
        var rows = Calculate(57, 31, 0);
        Assert.Equal(5, rows["maximum_life_leech_amount_per_leech"]);
        Assert.Equal(3, rows["maximum_mana_leech_amount_per_leech"]);
        Assert.Equal(0, rows["maximum_energy_shield_leech_amount_per_leech"]);
        Assert.Equal(0, rows["maximum_total_recovery_per_minute_from_energy_shield_leech"]);
        Assert.Equal(60000, rows["virtual_maximum_energy_shield_leech_rate_permyriad_per_minute"]);
    }

    [Fact]
    public void Every_generated_stat_exists_in_the_native_catalog()
    {
        foreach (var (stat, _) in LeechLimits.Compute(_ => 0, 1, 1, 1)) Assert.NotNull(Program.Stats.Key(stat));
    }
}

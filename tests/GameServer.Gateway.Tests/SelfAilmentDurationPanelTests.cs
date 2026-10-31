namespace GameServer.Gateway.Tests;

public sealed class SelfAilmentDurationPanelTests
{
    const string AllSelfDuration = "base_all_ailment_duration_on_self_+%";
    static readonly string[] Ailments = ["ignite", "chill", "freeze", "shock"];

    static Dictionary<string, int> Compute(IReadOnlyDictionary<string, int> raw) =>
        PanelStats.Compute(n => raw.GetValueOrDefault(n)).ToDictionary(v => v.Stat, v => v.Value);

    [Theory]
    [InlineData(-20, 20)]
    [InlineData(20, -20)]
    public void All_ailment_duration_has_the_opposite_sign_to_each_self_reduction_row(int source, int expected)
    {
        var panel = Compute(new Dictionary<string, int> { [AllSelfDuration] = source });
        foreach (string ailment in Ailments)
            Assert.Equal(expected, panel[$"self_{ailment}_duration_-%"]);
    }

    [Fact]
    public void Typed_elemental_and_all_ailment_sources_compose_once_from_the_unchanged_raw_input()
    {
        var raw = new Dictionary<string, int>
        {
            [AllSelfDuration] = -20,
            ["self_elemental_status_duration_-%"] = -50,
        };
        foreach (string ailment in Ailments)
            raw[$"base_self_{ailment}_duration_-%"] = 20;
        var before = raw.ToArray();

        var first = Compute(raw);
        var again = Compute(raw);
        foreach (string ailment in Ailments)
        {
            Assert.Equal(-10, first[$"self_{ailment}_duration_-%"]); // 20 - 50 - (-20).
            Assert.Equal(-10, again[$"self_{ailment}_duration_-%"]);
        }
        Assert.Equal(before, raw.ToArray());
    }

    [Fact]
    public void Exact_cancellation_omits_zero_self_duration_results()
    {
        var raw = new Dictionary<string, int> { [AllSelfDuration] = 20 };
        foreach (string ailment in Ailments)
            raw[$"base_self_{ailment}_duration_-%"] = 20;
        var panel = Compute(raw);
        foreach (string ailment in Ailments)
            Assert.False(panel.ContainsKey($"self_{ailment}_duration_-%"));
    }

    [Fact]
    public void A_supplied_display_seed_stays_additive_in_the_pure_sum()
    {
        // Gateway developer replacements deliberately have separate precedence;
        // this helper contract adds a supplied non-replacement display seed.
        var panel = Compute(new Dictionary<string, int>
        {
            [AllSelfDuration] = -20,
            ["self_ignite_duration_-%"] = 7,
        });
        Assert.Equal(27, panel["self_ignite_duration_-%"]);
        foreach (string ailment in Ailments.Skip(1))
            Assert.Equal(20, panel[$"self_{ailment}_duration_-%"]);
    }

    [Fact]
    public void Sign_inversion_uses_wide_arithmetic_and_saturates_only_the_completed_sum()
    {
        var raw = new Dictionary<string, int> { [AllSelfDuration] = int.MinValue };
        var saturated = Compute(raw);
        foreach (string ailment in Ailments)
            Assert.Equal(int.MaxValue, saturated[$"self_{ailment}_duration_-%"]);

        foreach (string ailment in Ailments)
            raw[$"base_self_{ailment}_duration_-%"] = int.MinValue;
        var cancelled = Compute(raw);
        foreach (string ailment in Ailments)
            Assert.False(cancelled.ContainsKey($"self_{ailment}_duration_-%"));
    }
}

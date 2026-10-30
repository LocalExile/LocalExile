namespace GameServer.Gateway.Tests;

public sealed class SpecialChargeMirrorPanelTests
{
    static Dictionary<string, int> Mirror(Dictionary<string, int> values) =>
        ChargePanelStats.MirroredMaximums(n => values.GetValueOrDefault(n)).ToDictionary(v => v.Stat, v => v.Value);

    [Fact]
    public void No_flags_emit_three_zero_gates_and_do_not_invent_direct_special_maxima()
    {
        var rows = Mirror(new() { ["maximum_brutal_charges"] = 999, ["virtual_maximum_endurance_charges"] = 4 });
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Values, v => Assert.Equal(0, v));
        Assert.Equal(3, ChargePanelStats.Compute(_ => 0).Count);
    }

    [Fact]
    public void Three_real_flags_mirror_only_the_corresponding_completed_unequal_maximum()
    {
        var values = new Dictionary<string, int>
        {
            ["maximum_brutal_charges_is_equal_to_maximum_endurance_charges"] = 1,
            ["maximum_affliction_charges_is_equal_to_maximum_frenzy_charges"] = 1,
            ["maximum_absorption_charges_is_equal_to_maximum_power_charges"] = 1,
            ["virtual_maximum_endurance_charges"] = 4,
            ["virtual_maximum_frenzy_charges"] = 5,
            ["virtual_maximum_power_charges"] = 6,
            ["max_endurance_charges"] = 999, // Completed input is authoritative, not a re-sum.
        };
        for (int i = 0; i < 2; i++)
        {
            var rows = Mirror(values);
            Assert.Equal(4, rows["virtual_maximum_brutal_charges"]);
            Assert.Equal(5, rows["virtual_maximum_affliction_charges"]);
            Assert.Equal(6, rows["virtual_maximum_absorption_charges"]);
        }
        values.Remove("maximum_brutal_charges_is_equal_to_maximum_endurance_charges");
        var cleared = Mirror(values);
        Assert.Equal(0, cleared["virtual_maximum_brutal_charges"]);
        Assert.Equal(5, cleared["virtual_maximum_affliction_charges"]);
        Assert.Equal(6, cleared["virtual_maximum_absorption_charges"]);
        values["virtual_maximum_frenzy_charges"] = 0;
        Assert.Equal(0, Mirror(values)["virtual_maximum_affliction_charges"]);
    }

    [Fact]
    public void Minimum_conversion_and_gain_instead_flags_are_not_maximum_mirrors()
    {
        var rows = Mirror(new()
        {
            ["modifiers_to_minimum_endurance_charges_instead_apply_to_brutal_charges"] = 1,
            ["gain_brutal_charges_instead_of_endurance_charges"] = 1,
            ["virtual_maximum_endurance_charges"] = 7,
        });
        Assert.All(rows.Values, v => Assert.Equal(0, v));
    }
}

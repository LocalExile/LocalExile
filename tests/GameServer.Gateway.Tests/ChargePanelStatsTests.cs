using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class ChargePanelStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<StatCatalog> Stats = new(() => new StatCatalog(Data()));

    static Dictionary<string, int> Compute(params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return ChargePanelStats.Compute(n => given.GetValueOrDefault(n)).ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Every_charge_stat_exists_in_Stats_dat()
    {
        foreach (var name in new[]
        {
            "virtual_maximum_endurance_charges", "virtual_maximum_frenzy_charges", "virtual_maximum_power_charges",
            "max_endurance_charges", "max_frenzy_charges", "max_power_charges",
            "maximum_power_and_endurance_charges_+", "maximum_power_and_frenzy_charges_+",
        })
            Assert.NotNull(Stats.Value.Key(name));
    }

    [Fact]
    public void A_character_starts_with_three_of_each()
    {
        var rows = Compute();
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(3, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(3, rows["virtual_maximum_power_charges"]);
    }

    [Fact]
    public void Maximum_charge_mods_add_to_their_row()
    {
        var rows = Compute(("max_endurance_charges", 2), ("max_frenzy_charges", 1), ("max_power_charges", 3));
        Assert.Equal(5, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(4, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(6, rows["virtual_maximum_power_charges"]);
    }

    [Fact]
    public void The_all_three_maximum_mod_counts_for_every_row()
    {
        // "+1 to Maximum Endurance, Frenzy and Power Charges" (maximum_frenzy_power_endurance_charges, four mods).
        Assert.NotNull(Stats.Value.Key("maximum_frenzy_power_endurance_charges"));
        var rows = Compute(("maximum_frenzy_power_endurance_charges", 1), ("max_power_charges", 1));
        Assert.Equal(4, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(4, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(5, rows["virtual_maximum_power_charges"]);
        // The gameplay maximum is the same number.
        Assert.Equal(4, Charges.LimitsOf(ChargeType.Frenzy, n => n == "maximum_frenzy_power_endurance_charges" ? 1 : 0).Maximum);
    }

    [Fact]
    public void Combined_maximum_charge_mods_count_for_both_rows()
    {
        // Warstaff base implicit: +1 to Maximum Power and Endurance Charges; "there is more": +1 to Maximum Power and
        // Frenzy Charges.
        var rows = Compute(("maximum_power_and_endurance_charges_+", 1), ("maximum_power_and_frenzy_charges_+", 1));
        Assert.Equal(4, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(4, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(5, rows["virtual_maximum_power_charges"]);
    }

    [Fact]
    public void A_maximum_never_shows_below_zero()
    {
        var rows = Compute(("max_endurance_charges", -10));
        Assert.Equal(0, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(3, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(3, rows["virtual_maximum_power_charges"]);
    }

    [Fact]
    public void Conditional_maximum_charge_stats_are_not_counted()
    {
        // Sublime Vision's "... while affected by Determination/Grace/Discipline" needs the aura, which this slice
        // does not model; only unconditional maximum-charge stats are counted.
        var rows = Compute(("maximum_endurance_charges_+_while_affected_by_determination", 1),
            ("maximum_frenzy_charges_+_while_affected_by_grace", 1),
            ("maximum_power_charges_+_while_affected_by_discipline", 1));
        Assert.Equal(3, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(3, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(3, rows["virtual_maximum_power_charges"]);
    }

    [Fact]
    public void Extreme_additive_inputs_do_not_wrap_before_the_clamp()
    {
        var rows = Compute(("max_power_charges", int.MaxValue),
            ("maximum_power_and_endurance_charges_+", int.MaxValue),
            ("max_frenzy_charges", int.MinValue), ("maximum_power_and_frenzy_charges_+", int.MinValue));
        Assert.Equal(int.MaxValue, rows["virtual_maximum_endurance_charges"]);
        Assert.Equal(0, rows["virtual_maximum_frenzy_charges"]);
        Assert.Equal(int.MaxValue, rows["virtual_maximum_power_charges"]);
    }

    [Fact]
    public void The_panel_reads_the_virtual_maximum_rows()
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables", "CharacterPanelStats.json")));
        var rows = table.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("Id").GetString()!, r => r);
        foreach (var (row, display) in new[]
        {
            ("EnduranceCharges", "virtual_maximum_endurance_charges"),
            ("FrenzyCharges", "virtual_maximum_frenzy_charges"),
            ("PowerCharges", "virtual_maximum_power_charges"),
        })
        {
            Assert.NotNull(Stats.Value.Key(display));
            int key = (int)Stats.Value.Key(display)!.Value;
            var keys = rows[row].GetProperty("StatsKeys1").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            // CharacterPanelStats.dat stores Stats.dat row indices; StatCatalog's key is the row + 1.
            Assert.Contains(key - 1, keys);
        }
    }
}

using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class ChargePanelStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
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

}

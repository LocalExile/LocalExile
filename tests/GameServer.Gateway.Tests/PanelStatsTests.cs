using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PanelStatsTests
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
        return PanelStats.Compute(n => given.GetValueOrDefault(n)).ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Every_stat_in_the_table_exists_in_Stats_dat()
    {
        var names = PanelStats.Sums.SelectMany(s => s.Sources.Concat(s.Local ?? []).Select(x => x.Name).Prepend(s.Display));
        Assert.DoesNotContain(names, n => Stats.Value.Key(n) is null);
        Assert.Equal(PanelStats.Sums.Length, PanelStats.Sums.Select(s => s.Display).Distinct().Count());
    }

    static Dictionary<string, int> Recovery(params (string Stat, int Value)[] stats) =>
        Recovery(1000, 500, stats);

    static Dictionary<string, int> Recovery(uint maxLife, uint maxMana, params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return PanelStats.Recovery(n => given.GetValueOrDefault(n), maxLife, maxMana)
            .ToDictionary(s => s.Stat, s => s.Value);
    }

}

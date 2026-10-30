using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemModStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(ItemCatalog Catalog, StatCatalog Stats)> Setup = new(() => (new ItemCatalog(Data()), new StatCatalog(Data())));
    static ModCatalog Mods => Setup.Value.Catalog.Mods;

    static Item Ring(params (string Id, int[] Values)[] mods) =>
        new(1, ["Base", "Mods"], 0) { Mods = [.. mods.Select(m => Mods.ById(m.Id)!.With(ModSection.Explicit, m.Values, Random.Shared))] };

    [Fact]
    public void Global_stats_of_equipped_items_are_summed_by_client_key()
    {
        var stats = ItemModStats.Global([Ring(("IncreasedLife1", [9])), Ring(("IncreasedLife1", [6]))], Mods, Setup.Value.Stats);
        Assert.Equal(15, stats[Setup.Value.Stats.Key("base_maximum_life")!.Value]);
    }

}

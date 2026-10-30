using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemRollsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(ItemCatalog Catalog, ItemRolls Rolls)> Setup = new(() =>
    {
        var catalog = new ItemCatalog(Data());
        return (catalog, new ItemRolls(catalog, Data()));
    });

    static ItemCatalog Catalog => Setup.Value.Catalog;
    static ItemRolls Rolls => Setup.Value.Rolls;
    static ItemCatalog.BaseType Wand => Catalog.Row(384)!;

    [Fact]
    public void A_wand_has_its_chain_tags()
    {
        Assert.Superset(new HashSet<string> { "default", "weapon", "onehand", "wand" }, new HashSet<string>(Wand.Tags));
    }

    static List<ModCatalog.Mod> Explicits(Item item) =>
        [.. item.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => Catalog.Mods.ByHash(m.Hash)!)];
}

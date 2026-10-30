using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Mageblood's item markers and which flasks it applies (Program.Mageblood): the leftmost magic utility flasks only.
public class MagebloodTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/Flasks.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("Flasks.json");
    }

    static readonly Lazy<(ItemCatalog Items, Item Belt)> Setup = new(() =>
    {
        string data = Data();
        var items = new ItemCatalog(data);
        return (items, new UniqueCatalog(items, data).Create("Mageblood", new Random(1))!);
    });

    [Fact]
    public void Mageblood_is_recognised_by_its_unique_word_and_mod_flask_count()
    {
        var (items, belt) = Setup.Value;

        Assert.True(Program.IsMageblood(belt));
        Assert.False(Program.IsMageblood(ItemCatalog.Create(items.Find("Heavy Belt")!)));
        Assert.InRange(Program.MagebloodFlaskCount(belt), 2, 4);
    }

}

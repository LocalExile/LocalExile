namespace GameServer.Gateway.Tests;

// What Stibnite, Sulphur and Basalt Flasks make on use (FlaskGrounds): smoke cloud (Blind), consecrated ground, taunt.
public class FlaskGroundsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));

    static List<(FlaskGrounds.Kind, int)> OnUse(string flask)
    {
        var item = ItemCatalog.Create(Catalog.Value.Find(flask)!);
        return [.. FlaskGrounds.OnUse(ItemModStats.Local(item, Catalog.Value.Mods))];
    }

    [Fact]
    public void The_flasks_implicits_make_their_grounds_with_the_tables_radii()
    {
        // BaseItemTypes implicits: UtilityFlaskSmokeCloud 16, UtilityFlaskConsecrate 18, UtilityFlaskTaunt_ 30.
        Assert.Equal([(FlaskGrounds.Kind.SmokeCloud, 16)], OnUse("Stibnite Flask"));
        Assert.Equal([(FlaskGrounds.Kind.ConsecratedGround, 18)], OnUse("Sulphur Flask"));
        Assert.Equal([(FlaskGrounds.Kind.Taunt, 30)], OnUse("Basalt Flask"));
        Assert.Empty(OnUse("Quicksilver Flask"));
        // Aquamarine's chilled ground is not made (monsters cannot be chilled in step with the client yet).
        Assert.Empty(OnUse("Aquamarine Flask"));
    }

}

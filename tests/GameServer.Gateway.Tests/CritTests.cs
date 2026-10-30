namespace GameServer.Gateway.Tests;

public class CritTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void A_weapons_base_critical_chance_comes_from_weapon_types()
    {
        var catalog = new ItemCatalog(Data());
        var wand = catalog.Row(384);
        Assert.NotNull(wand);
        Assert.Equal(830, catalog.WeaponCritical(wand));
    }

}

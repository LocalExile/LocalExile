namespace GameServer.Gateway.Tests;

/// <summary>
/// The shop portal effects the server applies (Cosmetics.cs). The tables are the extracted game data, so these read
/// data/tables like the other catalogue tests; without it (a fresh clone before scripts/dev/extract-data.ps1) the
/// catalogue is empty rather than broken.
/// </summary>
public class CosmeticTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly CosmeticCatalog Catalogue = new(Data());

    [Fact]
    public void A_portal_effect_has_the_shop_name_and_its_animated_object()
    {
        var orange = Catalogue.Portals.Single(c => c.Index == 1);
        Assert.Equal("Orange Portal Effect", orange.Name);
        Assert.Equal("Metadata/Effects/Microtransactions/Town_Portals/supporter_01/rig.ao", orange.Ao);
        Assert.True(Catalogue.Portals.Count > 200);
    }

}

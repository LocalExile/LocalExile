using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AtlasCatalogTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;

    [Fact]
    public void Loads_the_normal_atlas_layouts_and_a_starting_default()
    {
        var atlas = new AtlasCatalog(Catalog);
        Assert.True(atlas.Layouts.Count > 100);
        Assert.NotNull(atlas.Default);
        Assert.True(atlas.Default!.Starting);
        Assert.Equal("Cells", atlas.Default.Name);
    }

}

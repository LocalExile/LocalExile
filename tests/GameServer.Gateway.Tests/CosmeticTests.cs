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
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
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

    [Fact]
    public void A_portal_is_found_by_its_number_name_or_part_of_its_name()
    {
        var byNumber = CosmeticCatalog.Find(Catalogue.Portals, "6");
        var byName = CosmeticCatalog.Find(Catalogue.Portals, "Black Hole Portal Effect");
        var byPart = CosmeticCatalog.Find(Catalogue.Portals, "black hole");
        Assert.NotNull(byNumber);
        Assert.Equal(byNumber, byName);
        Assert.Equal(byNumber, byPart);
        Assert.Equal("Black Hole Portal Effect", byName!.Name);
        Assert.EndsWith("Town_Portals/black_hole/rig.ao", byName.Ao, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_matches_an_unknown_name_and_an_empty_query()
    {
        Assert.Null(CosmeticCatalog.Find(Catalogue.Portals, "no such portal effect"));
        Assert.Null(CosmeticCatalog.Find(Catalogue.Portals, ""));
        Assert.Empty(CosmeticCatalog.Matches(Catalogue.Portals, "no such portal effect"));
        Assert.Empty(CosmeticCatalog.Matches(Catalogue.Portals, ""));
    }

    [Fact]
    public void A_fragment_that_several_effects_have_matches_all_of_them()
    {
        // Why /cosmetic lists its matches instead of applying the first: two portal effects contain "voidgate".
        var matches = CosmeticCatalog.Matches(Catalogue.Portals, "voidgate");
        Assert.True(matches.Count > 1);
        Assert.All(matches, c => Assert.Contains("voidgate", c.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(matches, c => c.Name == "Voidgate Portal Effect");
    }

    [Fact]
    public void A_number_an_exact_name_or_a_piece_of_one_names_its_one_effect()
    {
        var byNumber = Assert.Single(CosmeticCatalog.Matches(Catalogue.Portals, "6"));
        Assert.Equal(Catalogue.Portals.Single(c => c.Index == 6), byNumber);
        var byName = Assert.Single(CosmeticCatalog.Matches(Catalogue.Portals, "Black Hole Portal Effect"));
        Assert.Equal(byNumber, byName);   // Black Hole is index 6
        Assert.Equal(byNumber, Assert.Single(CosmeticCatalog.Matches(Catalogue.Portals, "black hole")));
    }

    [Fact]
    public void Every_portal_effect_plays_an_ao_the_client_has()
    {
        Assert.All(Catalogue.Portals, c =>
        {
            Assert.StartsWith("Metadata/Effects/", c.Ao, StringComparison.Ordinal);
            Assert.EndsWith(".ao", c.Ao, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
        Assert.Equal(Catalogue.Portals.Count, Catalogue.Portals.Select(c => c.Index).Distinct().Count());
    }

    [Fact]
    public void Without_the_tables_the_catalogue_is_empty()
    {
        var none = new CosmeticCatalog(Path.Combine(Path.GetTempPath(), "localexile-no-data"));
        Assert.Empty(none.Portals);
        Assert.Null(CosmeticCatalog.Find(none.Portals, "1"));
    }
}

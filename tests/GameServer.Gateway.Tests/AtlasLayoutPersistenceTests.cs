namespace GameServer.Gateway.Tests;

/// <summary>The map device's Atlas layout choice survives a Gateway restart (382): it lives in the character save, not in
/// the catalogue's memory.</summary>
public class AtlasLayoutPersistenceTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void The_selected_layout_is_read_back_from_the_character_save()
    {
        TestEnvironment.OpenOwnSaves("atlas");
        var catalog = new ItemCatalog("data");
        var atlas = new AtlasCatalog(catalog);
        var chosen = atlas.Layouts.First(l => l.Id != atlas.Default?.Id);
        Assert.Equal(atlas.Default?.Id, atlas.Selected("AtlasPersistence")?.Id);
        atlas.Select("AtlasPersistence", chosen);
        // A new catalogue (what a Gateway restart builds) reads the choice back from the save.
        var reopened = new AtlasCatalog(catalog);
        Assert.Equal(chosen.Id, reopened.Selected("AtlasPersistence")?.Id);
        Assert.Equal(chosen.Id, CharacterSaves.AtlasLayout("AtlasPersistence"));
        Assert.Equal(atlas.Default?.Id, reopened.Selected("SomeoneElse")?.Id);
    }
}

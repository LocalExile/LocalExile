namespace GameServer.Gateway.Tests;

public sealed class StrongboxTests
{
    // Currency on a closed box changes its rarity and chest mods as on an item, and goes on the wire with them; a Vaal Orb
    // locks it; an opened box takes nothing.
    [Fact]
    public void Currency_changes_a_closed_strongbox_like_an_item()
    {
        var type = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Arcanist")!;
        var catalog = Program.Items.Mods;
        var rng = new Random(7);
        (MapStrongboxes.Box? Box, string? Why) Use(MapStrongboxes.Box box, string currency) =>
            MapStrongboxes.Change(catalog, type, box, currency, 70, MapMods.None, rng);
        var normal = new MapStrongboxes.Box(1, 0, 100, 100, type.Id);

        Assert.Null(Use(normal, CurrencyEffects.OrbOfAlteration).Box);
        var magic = Use(normal, CurrencyEffects.OrbOfTransmutation).Box!;
        Assert.Equal(1, magic.Rarity);
        Assert.InRange(magic.Mods.Length, 1, 2);
        Assert.All(magic.Mods, m => Assert.Equal(StrongboxMods.ChestDomain, catalog.ByHash(m.Hash)!.Domain));
        Assert.NotEqual(StrongboxCatalog.Blob(type, 100, 100, false, false),
            StrongboxCatalog.Blob(type, 100, 100, false, false, magic.Rarity, [.. type.Mods, .. magic.Explicits]));
        var rare = Use(magic, CurrencyEffects.RegalOrb).Box!;
        Assert.Equal(2, rare.Rarity);
        Assert.Equal(magic.Mods.Length + 1, rare.Mods.Length);
        var scoured = Use(rare, CurrencyEffects.OrbOfScouring).Box!;
        Assert.Equal(0, scoured.Rarity);
        Assert.Empty(scoured.Mods);

        var corrupted = Use(rare, CurrencyEffects.VaalOrb).Box!;
        Assert.True(corrupted.Corrupted && corrupted.VaalOrb);
        Assert.Equal("the box is corrupted", Use(corrupted, CurrencyEffects.ChaosOrb).Why);
        Assert.Equal("the box is open", Use(normal with { Phase = 1 }, CurrencyEffects.OrbOfTransmutation).Why);
    }
}

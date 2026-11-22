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
        var annulled = Use(rare, CurrencyEffects.OrbOfAnnulment).Box!;
        Assert.Equal(rare.Mods.Length - 1, annulled.Mods.Length);
        Assert.All(annulled.Mods, m => Assert.Contains(rare.Mods, r => r.Hash == m.Hash));
        Assert.Equal(rare.Mods.Select(m => m.Hash), Use(rare, CurrencyEffects.DivineOrb).Box!.Mods.Select(m => m.Hash));
        var scoured = Use(rare, CurrencyEffects.OrbOfScouring).Box!;
        Assert.Equal(0, scoured.Rarity);
        Assert.Empty(scoured.Mods);

        var corrupted = Use(rare, CurrencyEffects.VaalOrb).Box!;
        Assert.True(corrupted.Corrupted);
        Assert.Equal(rare.Mods, corrupted.Mods);
        var chanced = Use(normal, CurrencyEffects.OrbOfChance).Box!;
        Assert.InRange(chanced.Rarity, 1, 3);
        Assert.NotEmpty(chanced.Mods);
        Assert.Equal(chanced.Rarity == 3, chanced.Unique is not null);
        Assert.Equal("the box is corrupted", Use(corrupted, CurrencyEffects.ChaosOrb).Why);
        Assert.Equal("the box is open", Use(normal with { Phase = 1 }, CurrencyEffects.OrbOfTransmutation).Why);
    }

    // A unique box is a UniqueChests row on its strongbox: rarity 3, its own model and its Words name on the wire.
    [Fact]
    public void Unique_strongboxes_come_from_the_unique_chests()
    {
        var kaom = StrongboxCatalog.FindUnique("KaomsCache")!;
        Assert.Equal("Kaom's Cache", kaom.Name);
        var type = StrongboxCatalog.All.Single(t => t.Row == kaom.ChestRow);
        Assert.Equal("Metadata/Chests/StrongBoxes/Armory", type.Id);
        Assert.DoesNotContain(StrongboxCatalog.Uniques, u => u.Id.StartsWith("Grandmaster", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0, 50), i => Assert.True(StrongboxCatalog.RollUnique(30, new Random(i))!.Value.Unique.MinLevel <= 30));
        var plain = StrongboxCatalog.Blob(type, 100, 100, false, false, 3, type.Mods, null, null);
        var named = StrongboxCatalog.Blob(type, 100, 100, false, false, 3, type.Mods, null, kaom);
        Assert.NotEqual(plain, named);
        // A non-normal box's inherent bonus: the hidden ChestMagic/ChestRare/ChestUnique mods.
        Assert.Equal(0, StrongboxMods.RarityStat(Program.Items.Mods, 0, "chest_hidden_item_quantity_+%"));
        Assert.Equal(100, StrongboxMods.RarityStat(Program.Items.Mods, 2, "chest_hidden_item_quantity_+%"));
        Assert.Equal(150, StrongboxMods.RarityStat(Program.Items.Mods, 3, "chest_hidden_item_rarity_+%"));
        var word = BitConverter.GetBytes(kaom.NameWord);
        Assert.True(named.AsSpan().IndexOf(word) >= 0 || named.AsSpan().IndexOf(word.Reverse().ToArray()) >= 0);
    }

    // The closed glow and the open effect a box plays come from the Chests row's ChestEffects row, one model per
    // rarity (the rows' white/blue/gold/brown sets; the small and big strongboxes have their own).
    [Fact]
    public void Chest_effects_come_from_the_chests_row_by_rarity()
    {
        var small = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Arcanist")!;
        Assert.Equal(2, small.ChestEffect);
        Assert.EndsWith("smlstrongboxwhiteloopfx.ao", StrongboxCatalog.ClosedEffect(small, 0));
        Assert.EndsWith("smlstrongboxblueloopfx.ao", StrongboxCatalog.ClosedEffect(small, 1));
        Assert.EndsWith("smlstrongboxgoldopenfx.ao", StrongboxCatalog.OpenEffect(small, 2));
        Assert.EndsWith("smlstrongboxbrownopenfx.ao", StrongboxCatalog.OpenEffect(small, 3));
        var big = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Large")!;
        Assert.Equal(1, big.ChestEffect);
        Assert.EndsWith("bigstrongboxwhiteloopfx.ao", StrongboxCatalog.ClosedEffect(big, 0));
        Assert.DoesNotContain("loop", StrongboxCatalog.OpenEffect(big, 3));
    }
}

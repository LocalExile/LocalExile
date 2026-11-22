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
        CheckArmoryCorruption();
    }

    static void CheckArmoryCorruption()
    {
        const string stat = "map_items_drop_corrupted_%";
        var box = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Armory")!;
        var conn = new ClientState(new(1, "strongbox", 100, 100, 20, 20, null, []), 1, 1) { Instance = new() };
        List<ItemDrops.Drop> Roll(int chance, bool mirrored = false, bool corrupted = false) => StrongboxLoot.Roll(conn, box,
            id => mirrored && id == "dropped_items_are_duplicated" ? 1 : 0, 83,
            new MapMods(new Dictionary<string, int> { [stat] = chance }), new Random(43), corrupted);

        var normal = new MapStrongboxes.Box(1, 0, 20, 20, box.Id);
        var vaaled = MapStrongboxes.Change(Program.Items.Mods, box, normal, CurrencyEffects.VaalOrb, 83, MapMods.None, new Random(43)).Box!;
        Assert.All(Roll(0, corrupted: vaaled.Corrupted), drop => Assert.True(drop.Item.Corrupted));
        var guaranteed = Roll(100);
        Assert.NotEmpty(guaranteed);
        Assert.All(guaranteed, drop => Assert.True(drop.Item.Corrupted));

        var paired = Roll(50, mirrored: true);
        Assert.Equal(guaranteed.Count * 2, paired.Count);
        Assert.Contains(paired, drop => drop.Item.Corrupted);
        Assert.Contains(paired, drop => !drop.Item.Corrupted);
        for (int i = 0; i < guaranteed.Count; i++)
        {
            Assert.True(paired[i].Item.Mirrored);
            Assert.Equal(paired[i].Item, paired[i + guaranteed.Count].Item);
        }

        conn.Instance.AreaStats(() => new Dictionary<uint, int>());
        Assert.All(Roll(100), drop => Assert.False(drop.Item.Corrupted));
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
}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class IdolRollTests
{
    [Fact]
    public void A_rare_Idol_rolls_three_or_four_of_its_own_modifiers_and_at_most_two_a_side()
    {
        var items = Program.Items;
        var rolls = new ItemRolls(items);
        string[] names = ["Minor Idol", "Kamasan Idol", "Totemic Idol", "Noble Idol", "Burial Idol", "Conqueror Idol"];
        foreach (string name in names)
        {
            var type = items.Find(name)!;
            Assert.Equal("AtlasRelic", type.Class);
            for (int seed = 0; seed < 40; seed++)
            {
                var made = ItemCatalog.Create(type, random: new Random(seed)) with { ItemLevel = 83 };
                var rare = rolls.Roll(made, ItemRarity.Rare, new Random(seed));
                var affixes = rare.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => items.Mods.ByHash(m.Hash)!).ToList();
                // Idols share the jewels' caps: the ordinary six would give an Idol more than the client's Idols have.
                Assert.InRange(affixes.Count, 3, 4);
                Assert.InRange(affixes.Count(m => m.GenerationType == ItemRolls.Prefix), 0, 2);
                Assert.InRange(affixes.Count(m => m.GenerationType == ItemRolls.Suffix), 0, 2);
                Assert.All(affixes, m => Assert.Equal(type.ModDomain, m.Domain));
                Assert.Equal(affixes.Count, affixes.SelectMany(m => m.Families).Distinct().Count());
                Assert.Equal((2, 2), rolls.RareMaxAffixes(rare));
            }
        }
    }

    [Fact]
    public void Every_unique_Idol_the_client_limits_is_a_Minor_Idol_with_its_own_modifiers()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var words = GameServer.Core.ClientTables.Table("Words");
        var names = GameServer.Core.ClientTables.Table("UniqueMapRelicLimits").Select(r => words[r.Int("Idol")].Text("Text")!).ToList();
        Assert.Equal(41, names.Count);
        var mods = new HashSet<ushort>();
        foreach (string name in names)
        {
            var definition = uniques.All.Single(d => d.Name == name);
            Assert.True(definition.EncounterOnly, name);   // not in the ordinary unique drops: only where Idols drop
            var made = uniques.Create(definition, new Random(7))!;
            Assert.Equal(("Minor Idol", ItemRarity.Unique), (items.ByHash(made.TypeHash)!.Name, made.Rarity));
            Assert.Equal(words.First(w => w.Int("Wordlist") == 6 && w.Text("Text") == name).UInt("HASH32"), Assert.Single(made.NameWords));
            var own = made.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => items.Mods.ByHash(m.Hash)!).ToList();
            // Twisted Wish: its own line and three of its seventeen reward lines; every other one has one modifier.
            Assert.Equal(name == "Twisted Wish" ? 4 : 1, own.Count);
            Assert.All(own, m => Assert.Equal(37, m.Domain));
            Assert.Equal(own.Count, own.Select(m => m.Hash).Distinct().Count());
            mods.Add(own[0].Hash);
        }
        Assert.Equal(41, mods.Count);
    }

    [Fact]
    public void Inventory_placement_refusal_receives_the_replaced_item_so_swapping_onto_the_same_unique_succeeds()
    {
        const byte idolRow = 62;
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var def = uniques.All.First(d => d.Name == "Twisted Wish");
        var first = uniques.Create(def, new Random(1))!;
        var second = uniques.Create(def, new Random(2))!;
        var other = uniques.Create(uniques.All.First(d => d.Name == "Black Thumb"), new Random(3))!;
        var inventories = new PlayerInventories();
        var conn = new ClientState(new(1, "idol-swap", 100, 100, 100, 100, null, []), 1, 1);
        PlayerInventories.Shape? ShapeOf(Item item) => items.ByHash(item.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var placedFirst = inventories.Add(idolRow, 0, 0, first);
        var placedOther = inventories.Add(idolRow, 1, 0, other);
        inventories.Add(PlayerInventories.Cursor, 0, 0, second);
        // Simulate a per-unique limit subscriber (like IdolsModule) through GameEvents.InventoryPlacementRefusal.
        using var sub = GameEvents.InventoryPlacementRefusal.Add("Game", p =>
            !ReferenceEquals(p.Conn, conn) || p.Row != idolRow || p.Item.Rarity != ItemRarity.Unique || p.Item.NameWords.Count == 0 ? null
            : inventories.Items(idolRow).Any(i => (p.Replaced is null || i.Id != p.Replaced.Id)
                && i.Item.Rarity == ItemRarity.Unique && i.Item.NameWords.Contains(p.Item.NameWords[0]))
                ? "limited to 1" : null);
        // Placing the second copy into an empty cell has no replaced item and is refused.
        Assert.Null(inventories.DisplacedByPlace(idolRow, 2, 0, ShapeOf));
        Assert.Equal("limited to 1", GameEvents.FirstInventoryRefusal(conn, idolRow, second, inventories.DisplacedByPlace(idolRow, 2, 0, ShapeOf)));
        // Swapping onto a different unique while the first copy sits elsewhere is still refused.
        Assert.Equal(placedOther.Id, inventories.DisplacedByPlace(idolRow, 1, 0, ShapeOf)?.Id);
        Assert.Equal("limited to 1", GameEvents.FirstInventoryRefusal(conn, idolRow, second, inventories.DisplacedByPlace(idolRow, 1, 0, ShapeOf)));
        Assert.Equal("limited to 1", GameEvents.FirstInventoryRefusal(conn, idolRow, second, inventories.DisplacedByMerge(idolRow, placedOther.Id, ShapeOf)));
        // Swapping onto the existing copy of the same unique passes replaced = placedFirst and succeeds (both 0x2a and 0x4a).
        Assert.Equal(placedFirst.Id, inventories.DisplacedByPlace(idolRow, 0, 0, ShapeOf)?.Id);
        Assert.Equal(placedFirst.Id, inventories.DisplacedByMerge(idolRow, placedFirst.Id, ShapeOf)?.Id);
        Assert.Null(GameEvents.FirstInventoryRefusal(conn, idolRow, second, inventories.DisplacedByPlace(idolRow, 0, 0, ShapeOf)));
        Assert.Null(GameEvents.FirstInventoryRefusal(conn, idolRow, second, inventories.DisplacedByMerge(idolRow, placedFirst.Id, ShapeOf)));
        var swap = inventories.Place(idolRow, 0, 0, ShapeOf, out _);
        Assert.NotNull(swap);
        Assert.Equal(placedFirst.Id, swap.Displaced?.Id);
    }
}

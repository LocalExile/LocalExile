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
}

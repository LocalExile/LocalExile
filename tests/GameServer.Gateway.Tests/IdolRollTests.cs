using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The Idols' base types are the client's; their unique Idols and drops are the idols mod's (LocalExile-Mods),
/// with its tests.</summary>
public class IdolRollTests
{
    [Fact]
    public void A_rare_Idol_rolls_three_or_four_of_its_own_modifiers_and_at_most_two_a_side()
    {
        var items = Program.Items;
        var rolls = new ItemRolls(items);
        Assert.Equal((3, 3), rolls.RareMaxAffixes(ItemCatalog.Create(items.Find("Minor Idol")!)));
        // As the idols mod does on load: the class has the jewels' caps while an owner asks for them.
        using var caps = ItemRolls.UseJewelAffixCaps(new ModuleLifetime("Idols"), "AtlasRelic");
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
}

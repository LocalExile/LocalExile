namespace GameServer.Gateway.Tests;

// 3.29 town gem vendors (19-gems.md §5): the level-1 gems of completed quest offers, by class; Siosa and Lilly ignore
// the class restriction (Lilly also the quest completion), and the 3.29 cross-class supports are added.
public class GemVendorStockTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/QuestRewardOffers.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("QuestRewardOffers.json");
    }

    static readonly Lazy<(ItemCatalog Items, GemVendorStock Stock)> Setup = new(() =>
    {
        string data = Data();
        var items = new ItemCatalog(data);
        return (items, new GemVendorStock(items, QuestRewardCatalog.Load(data), NpcCatalog.Load(data), new SkillCatalog(data)));
    });

    const string Nessa = "Metadata/NPC/Act1/Nessa", Yeena = "Metadata/NPC/Act2/Yeena",
        Siosa = "Metadata/NPC/Act3/Siosa", Lilly = "Metadata/NPC/Act6/Lilly";

    static IReadOnlyList<int> Stock(string npc, int classRow, params string[] flags) =>
        Setup.Value.Stock.Stock(npc, classRow, new HashSet<string>(flags));

    static HashSet<string> Names(IReadOnlyList<int> rows) =>
        [.. rows.Select(r => Setup.Value.Items.Row(r)!.Name)];

    [Fact]
    public void The_vendors_are_the_currency_flask_and_jewellery_ones()
    {
        foreach (string npc in new[] { Nessa, "Metadata/NPC/Act2/Yeena", "Metadata/NPC/Act3/ClarissaTown",
                     "Metadata/NPC/Act4/PetarusVanja", "Metadata/NPC/Act5/Lani", "Metadata/NPC/Act6/Lilly",
                     "Metadata/NPC/Act7/YeenaTown", "Metadata/NPC/Act8/ClarissaTown", "Metadata/NPC/Act9/PetarusVanja",
                     "Metadata/NPC/Act10/Lani", "Metadata/NPC/Epilogue/Lilly", Siosa })
            Assert.True(GemVendorStock.Sells(npc), npc);
        foreach (string npc in new[] { "Metadata/NPC/Act1/Tarkleigh", "Metadata/NPC/Act1/Bestel",
                     "Metadata/NPC/Act4/Kira", "Metadata/NPC/Act2/Greust" })
            Assert.False(GemVendorStock.Sells(npc), npc);
    }

}

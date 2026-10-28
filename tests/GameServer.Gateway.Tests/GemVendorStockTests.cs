namespace GameServer.Gateway.Tests;

// 3.29 town gem vendors (19-gems.md §5): the level-1 gems of completed quest offers, by class; Siosa and Lilly ignore
// the class restriction (Lilly also the quest completion), and the 3.29 cross-class supports are added.
public class GemVendorStockTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
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
    static readonly int Witch = 1, Marauder = 0;

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

    [Fact]
    public void A_gem_vendor_sells_nothing_before_a_quest_offers_gems()
    {
        Assert.Empty(Stock(Nessa, Witch));
        // a1q1's reward becomes available when Hillock dies; its "taken" flag works too.
        Assert.NotEmpty(Stock(Nessa, Witch, "a1q1-KilledHillock"));
        Assert.NotEmpty(Stock(Nessa, Witch, "a1q1-QuestRewardTaken"));
    }

    [Fact]
    public void The_vendor_sells_its_own_classes_gems_from_a_completed_offer()
    {
        var witch = Names(Stock(Nessa, Witch, "a1q1-QuestRewardTaken"));
        Assert.Contains("Freezing Pulse", witch);
        Assert.DoesNotContain("Ground Slam", witch); // Marauder's
        var marauder = Names(Stock(Nessa, Marauder, "a1q1-QuestRewardTaken"));
        Assert.Contains("Ground Slam", marauder);
        Assert.DoesNotContain("Freezing Pulse", marauder);
        Assert.All(Stock(Nessa, Witch, "a1q1-QuestRewardTaken"), row =>
            Assert.NotNull(Setup.Value.Items.Gem(Setup.Value.Items.Row(row)!)));
    }

    [Fact]
    public void A1q4_adds_its_six_class_gems_to_nessa()
    {
        Assert.Contains("Frost Bomb", Names(Stock(Nessa, Witch, "a1q4-QuestRewardTaken")));
        Assert.Contains("Flame Wall", Names(Stock(Nessa, Witch, "a1q4-QuestRewardTaken")));
        // Before the glyphs the offer is not available.
        Assert.DoesNotContain("Frost Bomb", Names(Stock(Nessa, Witch, "a1q1-QuestRewardTaken")));
    }

    [Fact]
    public void Siosa_needs_his_quest_and_ignores_the_class()
    {
        Assert.Empty(Stock(Siosa, Witch));
        var all = Names(Stock(Siosa, Witch, GemVendorStock.SiosaUnlock, "a1q4-QuestRewardTaken"));
        Assert.Contains("Frost Bomb", all);   // Witch's
        Assert.Contains("Decoy Totem", all);  // Marauder's a1q4 gem
    }

    [Fact]
    public void Lilly_needs_her_unlock_and_sells_the_whole_game_to_every_class()
    {
        Assert.Empty(Stock(Lilly, Marauder));
        var all = Names(Stock(Lilly, Marauder, GemVendorStock.LillyUnlock));
        Assert.Contains("Freezing Pulse", all);  // a1q1 Witch gem, no quest done
        Assert.Contains("Void Sphere", all);     // a4q2 gem, no quest done
        Assert.Contains("Ground Slam", all);
    }

    [Fact]
    public void Gem_prices_follow_the_level_one_requirement_tiers()
    {
        var (items, stock) = Setup.Value;
        (string Currency, int Amount) P(string name) => stock.Price(items.Find(name)!);
        // The official vendor tiers: requirement 1-7 Scroll of Wisdom, 8-15 Orb of Transmutation, 16-27 Orb of Alteration,
        // 28-37 Orb of Chance, 38+ Orb of Alchemy; one of the currency.
        Assert.Equal("Metadata/Items/Currency/CurrencyIdentification", P("Perforate").Currency);        // 1
        Assert.Equal("Metadata/Items/Currency/CurrencyIdentification", P("Frostblink").Currency);        // 4
        Assert.Equal("Metadata/Items/Currency/CurrencyUpgradeToMagic", P("Arrow Nova Support").Currency); // 8
        Assert.Equal("Metadata/Items/Currency/CurrencyUpgradeToMagic", P("Chain Hook").Currency);        // 12
        Assert.Equal("Metadata/Items/Currency/CurrencyRerollMagic", P("Flesh and Stone").Currency);      // 16
        Assert.Equal("Metadata/Items/Currency/CurrencyRerollMagic", P("Pride").Currency);                // 24
        Assert.Equal("Metadata/Items/Currency/CurrencyUpgradeRandomly", P("Pestilent Strike").Currency); // 28
        Assert.Equal("Metadata/Items/Currency/CurrencyUpgradeRandomly", P("Summon Carrion Golem").Currency); // 34
        Assert.Equal("Metadata/Items/Currency/CurrencyUpgradeToRare", P("Barrage Support").Currency);    // 38
        Assert.All(new[] { "Perforate", "Pride", "Barrage Support" }, name => Assert.Equal(1, P(name).Amount));
        Assert.Equal(0, GemVendorStock.Tier(-1));
        Assert.Equal(4, GemVendorStock.Tier(38));
    }

    [Fact]
    public void The_3_29_cross_class_supports_are_sold_by_nessa_and_yeena()
    {
        // All classes can buy Faster Attacks from Nessa after The Caged Brute, and Faster Casting from Yeena after
        // Sharp and Cruel; neither gem is in the Witch/Marauder offer lists for those quests.
        Assert.Contains("Faster Attacks Support", Names(Stock(Nessa, Witch, "a1q2-QuestRewardTaken")));
        Assert.Contains("Faster Casting Support", Names(Stock(Yeena, Marauder, "a2q4-QuestRewardTaken")));
        Assert.DoesNotContain("Faster Attacks Support", Names(Stock(Nessa, Witch, "a1q1-QuestRewardTaken")));
    }
}

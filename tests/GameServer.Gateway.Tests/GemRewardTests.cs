namespace GameServer.Gateway.Tests;

// Gems from quest rewards (12-quests.md, 19-gems.md "Getting gems"): the QuestRewardOffers table decides which offers are
// available, not a hardcoded allow list. Every act's gem offer is level-1 gems filtered by class; items that are not
// modelled yet (offer 8's magic armours, offer 15's magic belts) are skipped.
public class GemRewardTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/QuestRewardOffers.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("QuestRewardOffers.json");
    }

    [Fact]
    public void Every_acts_gem_reward_offer_is_available_for_the_class()
    {
        var data = Data();
        var catalog = QuestRewardCatalog.Load(data);
        var items = new ItemCatalog(data);

        // Witch (Characters row 1): the six a1q4 Glyph gems and each act's later gem offers. Some offers also carry items
        // that are Ruthless-only (offer 15's magic belts, offer 24's Uncarved Gemstone), which stay out of regular mode.
        var choices = new Dictionary<int, int> { [3] = 6, [4] = 1, [7] = 6, [10] = 9, [13] = 9, [15] = 13, [18] = 13, [23] = 16, [24] = 15, [29] = 10, [30] = 10 };
        var constructible = new Dictionary<int, int>(choices);
        foreach (var (offer, count) in choices)
        {
            var all = catalog.Choices(offer, 1);
            Assert.Equal(count, all.Count);
            var built = all.Select(c => (Choice: c, Item: QuestRewardCatalog.Create(c, items))).Where(c => c.Item is not null).ToArray();
            Assert.Equal(constructible[offer], built.Length);
        }
        Assert.Equal("a1q4-QuestRewardTaken", catalog.Find(3)!.TakenFlag);
    }

    [Fact]
    public void Other_classes_get_their_own_offer_choices()
    {
        var data = Data();
        var catalog = QuestRewardCatalog.Load(data);
        var items = new ItemCatalog(data);
        var witch = catalog.Choices(3, 1).Select(c => c.ItemRow).ToHashSet();
        var marauder = catalog.Choices(3, 0).Select(c => c.ItemRow).ToHashSet();
        Assert.NotEmpty(witch);
        Assert.NotEmpty(marauder);
        Assert.NotEqual(witch, marauder);
        Assert.All(catalog.Choices(3, 0), c => Assert.NotNull(QuestRewardCatalog.Create(c, items)));
    }
}

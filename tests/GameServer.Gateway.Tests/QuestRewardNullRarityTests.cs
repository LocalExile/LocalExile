using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class QuestRewardNullRarityTests
{
    [Fact]
    public void Levelled_choice_without_rarity_builds_a_normal_item_and_rarity_rows_still_roll()
    {
        var items = new ItemCatalog("data");
        var rewards = QuestRewardCatalog.Load();
        var rolls = new ItemRolls(items);
        // Offer 132 (DeepwaterCraftingTutorial2): one Coral Ring, item level 11, no rarity (QuestRewards row 1342).
        var ring = Assert.Single(rewards.Choices(132, 0));
        Assert.Equal(747, ring.ItemRow);
        var ringItem = QuestRewardCatalog.Create(ring, items);
        Assert.NotNull(ringItem);
        Assert.Equal(items.Row(747)!.Hash, ringItem.TypeHash);
        Assert.Equal(ItemRarity.Normal, ringItem.Rarity);
        Assert.Equal((byte)ring.Level, ringItem.ItemLevel);
        // Offers 16/32 are gems, whose level is the gem's level: still unbuildable here.
        var gem = rewards.Choices(16, 0).First();
        Assert.Null(QuestRewardCatalog.Create(gem, items));
        // Offer 14: a rare belt at item level 15 still goes through the rolls branch.
        var belt = rewards.Choices(14, 0).First(c => c.Rarity is 2);
        var beltItem = QuestRewardCatalog.Create(belt, items, rolls);
        Assert.NotNull(beltItem);
        Assert.Equal(items.Row(894)!.Hash, beltItem.TypeHash);
        Assert.Equal(ItemRarity.Rare, beltItem.Rarity);
        Assert.Equal((byte)belt.Level, beltItem.ItemLevel);
    }
}

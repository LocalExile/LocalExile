using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public sealed class BanditChoiceTests
{
    [Fact]
    public void HelpRewardRequiresBothRivalsAndCanBeClaimedOnce()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        var conn = fixture.Conn;
        var name = conn.Character!;
        var oak = BanditChoices.Oak;
        var items = Game.ItemsOf(conn);
        CharacterSaves.ChangeQuestFlags(name, [oak.Ally], true);
        var alira = Game.ItemsFor(conn).Find(BanditChoices.AmuletId(BanditChoices.Alira))!;
        var kraityn = Game.ItemsFor(conn).Find(BanditChoices.AmuletId(BanditChoices.Kraityn))!;
        const byte main = PlayerInventories.MainInventory;

        items.Add(main, 0, 0, ItemCatalog.Create(alira));
        Assert.Null(BanditChoices.ClaimReward(conn, oak).Result);
        Assert.Single(items.Items(main));

        items.Add(main, 1, 0, ItemCatalog.Create(kraityn));
        var result = Assert.IsType<BanditChoices.Claim>(BanditChoices.ClaimReward(conn, oak).Result);
        Assert.Equal(2, result.Consumed.Removed.Count);
        var saved = CharacterSaves.Load(name)!.QuestFlags!;
        Assert.Contains(oak.Reward, saved);
        Assert.Contains("a2b-SeenApexReward", saved);
        Assert.DoesNotContain("a2q7-QuestRewardTaken", saved);
        Assert.Equal("a2q8-HaveApex", Game.QuestFlagsOf(conn, Assert.Single(items.Items(main)).Item)!.Grant);
        Assert.Null(BanditChoices.ClaimReward(conn, oak).Result);
        Assert.Single(items.Items(main));
    }

    [Fact]
    public void HelpTurnInWaitsIfTheApexDoesNotFit()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        var conn = fixture.Conn;
        var name = conn.Character!;
        var alira = BanditChoices.Alira;
        var items = Game.ItemsOf(conn);
        CharacterSaves.ChangeQuestFlags(name, [alira.Ally], true);
        var oak = Game.ItemsFor(conn).Find(BanditChoices.AmuletId(BanditChoices.Oak))!;
        var kraityn = Game.ItemsFor(conn).Find(BanditChoices.AmuletId(BanditChoices.Kraityn))!;
        var currency = Game.ItemsFor(conn).Find("Orb of Transmutation")!;
        const byte main = PlayerInventories.MainInventory;
        const byte extra = PlayerInventories.ExpandedInventory;
        for (byte y = 0; y < 5; y++)
            for (byte x = 0; x < 12; x++)
                items.Add(main, x, y, ItemCatalog.Create(currency));
        items.Add(extra, 0, 0, ItemCatalog.Create(oak));
        items.Add(extra, 1, 0, ItemCatalog.Create(kraityn));

        Assert.Null(BanditChoices.ClaimReward(conn, alira).Result);
        Assert.Equal(2, items.Items(extra).Count);
        Assert.DoesNotContain(alira.Reward, CharacterSaves.Load(name)!.QuestFlags!);
        Assert.DoesNotContain("a2b-DeliveredAmulets", CharacterSaves.Load(name)!.QuestFlags!);

        items.Remove(main, items.Items(main).First().Id);
        Assert.NotNull(BanditChoices.ClaimReward(conn, alira).Result);
        Assert.Empty(items.Items(extra));
    }
}

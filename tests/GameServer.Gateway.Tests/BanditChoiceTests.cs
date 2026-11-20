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
        Assert.DoesNotContain("a2q7-QuestRewardTaken", saved);
        Assert.Empty(items.Items(main));   // the Apex is the leader's "Get the Apex" offer, not part of the hand-in
        Assert.Null(BanditChoices.ClaimReward(conn, oak).Result);
    }

    [Fact]
    public void HelpTurnInWorksWithAFullInventory()
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

        Assert.NotNull(BanditChoices.ClaimReward(conn, alira).Result);
        Assert.Empty(items.Items(extra));
        Assert.Equal(60, items.Items(main).Count);
        Assert.Contains(alira.Reward, CharacterSaves.Load(name)!.QuestFlags!);
    }
}

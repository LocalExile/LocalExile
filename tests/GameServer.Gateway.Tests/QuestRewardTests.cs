using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class QuestRewardTests : IDisposable
{
    readonly string folder = TestSaves.Catalog(new TestSaves.Type(123, "Gem"));
    readonly string character = "Reward" + Guid.NewGuid().ToString("N")[..12];
    ItemCatalog Catalog => new(folder);
    static Item Gem => new(123, ["Base"], 1);
    static PlayerInventories.Shape? Shape(Item _) => new(1, 1, "");
    public QuestRewardTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; Directory.Delete(folder, true); }
    [Fact] public void ClaimAndTakenFlagSurviveReloadAndCannotBeRepeated()
    {
        var inventory = PlayerInventories.For(character, Catalog);
        Assert.NotNull(QuestRewardCatalog.Claim(character, "a1q1-QuestRewardTaken", inventory, Gem, Shape));
        PlayerInventories.Forget(character);
        inventory = PlayerInventories.For(character, Catalog);
        Assert.Single(inventory.Items(PlayerInventories.MainInventory));
        Assert.Contains("a1q1-QuestRewardTaken", CharacterSaves.Load(character)!.QuestFlags!);
        Assert.Null(QuestRewardCatalog.Claim(character, "a1q1-QuestRewardTaken", inventory, Gem, Shape));
        Assert.Single(inventory.Items(PlayerInventories.MainInventory));
    }
    [Fact] public void FullInventoryLeavesRewardUntaken()
    {
        var inventory = PlayerInventories.For(character, Catalog);
        for(byte y=0;y<5;y++) for(byte x=0;x<12;x++) inventory.Add(0,x,y,Gem);
        Assert.Null(QuestRewardCatalog.Claim(character,"a1q1-QuestRewardTaken",inventory,Gem,Shape));
        Assert.DoesNotContain("a1q1-QuestRewardTaken",CharacterSaves.Load(character)!.QuestFlags ?? []);
    }
    [Fact] public void FailedFlagWriteRollsBackTheItemToo()
    {
        var inventory = PlayerInventories.For(character, Catalog);
        int writes = 0;
        Saves.FailWrite = key => key.Equals(character,StringComparison.OrdinalIgnoreCase) && ++writes == 2;
        Assert.ThrowsAny<Exception>(() => QuestRewardCatalog.Claim(character,"a1q1-QuestRewardTaken",inventory,Gem,Shape));
        Saves.FailWrite = null; PlayerInventories.Forget(character);
        Assert.Empty(PlayerInventories.For(character,Catalog).Items(0));
        Assert.DoesNotContain("a1q1-QuestRewardTaken",CharacterSaves.Load(character)!.QuestFlags ?? []);
    }
    [Fact] public void DialogueRequiresPrerequisitesAndHidesAfterClaim()
    {
        var talk = new NpcCatalog.Talk(25,0,2,"Hillock Reward","QuestReward","",[])
        { RequiredFlags=["a1q1-KilledHillock"],ExcludedFlags=["a1q1-QuestRewardTaken"],RewardOffer=0 };
        Assert.False(talk.Available(new HashSet<string>()));
        Assert.True(talk.Available(new HashSet<string>{"a1q1-KilledHillock"}));
        Assert.False(talk.Available(new HashSet<string>{"a1q1-KilledHillock","a1q1-QuestRewardTaken"}));
    }
}

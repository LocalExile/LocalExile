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
}

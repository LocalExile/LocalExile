using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class OpeningTutorialTests : IDisposable
{
    readonly string folder=TestSaves.Catalog(new TestSaves.Type(123,"Gem"));
    readonly string character="Beach"+Guid.NewGuid().ToString("N")[..12];
    static Item Gem => new(123,["Base"],1);
    public OpeningTutorialTests()=>TestSaves.Ensure();
    public void Dispose(){Saves.FailWrite=null;Directory.Delete(folder,true);}
    PlayerInventories Inventory()=>PlayerInventories.For(character,new ItemCatalog(folder));
    CharacterSaves.OpeningTutorial Tutorial()=>new(CharacterSaves.ToSaved(Gem),CharacterSaves.ToSaved(Gem),Id:Guid.NewGuid().ToString("N"));
    [Fact] public void GemsRequireTheirEncounterAndCanOnlyBeClaimedOnceAcrossReloads()
    {
        var inventory=Inventory(); CharacterSaves.SetTutorial(character,Tutorial());
        InventoryItem Put()=>inventory.Add(0,0,0,Gem);
        Assert.Null(TutorialObjects.Claim(character,false,Gem,Put));
        CharacterSaves.ChangeTutorial(character,t=>t with { ZombieKilled=true });
        Assert.NotNull(TutorialObjects.Claim(character,false,Gem,Put));
        PlayerInventories.Forget(character);inventory=Inventory();
        Assert.Null(TutorialObjects.Claim(character,false,Gem,Put));
        Assert.Null(TutorialObjects.Claim(character,true,Gem,Put));
        CharacterSaves.ChangeTutorial(character,t=>t with { ChestOpened=true });
        Assert.NotNull(TutorialObjects.Claim(character,true,Gem,()=>inventory.Add(0,1,0,Gem)));
        var state=CharacterSaves.Load(character)!.Tutorial!;
        Assert.True(state.SkillClaimed && state.SupportClaimed);Assert.Equal(2,inventory.Items(0).Count);
    }
}

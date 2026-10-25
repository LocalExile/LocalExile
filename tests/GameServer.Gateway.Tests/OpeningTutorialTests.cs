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
    [Fact] public void FailedClaimKeepsBothGemAndPendingState()
    {
        var inventory=Inventory();CharacterSaves.SetTutorial(character,Tutorial() with {ZombieKilled=true});
        int writes=0;Saves.FailWrite=key=>key==character&&++writes==2;
        Assert.ThrowsAny<Exception>(()=>TutorialObjects.Claim(character,false,Gem,()=>inventory.Add(0,0,0,Gem)));
        Saves.FailWrite=null;PlayerInventories.Forget(character);
        Assert.Empty(Inventory().Items(0));Assert.False(CharacterSaves.Load(character)!.Tutorial!.SkillClaimed);
    }
    [Fact] public void ChestUsesCapturedPlacementOnlyForItsAreaAndSeed()
    {
        string file=Path.Combine(folder,"1_1_1_seed7.objects.json");
        void Write(string area,int seed)=>File.WriteAllText(file,JsonSerializer.Serialize(new {area,seed,objects=new[]{new {objectType=TutorialObjects.Chest,model="Metadata/Test/Chest.ao",x=123,y=456,rotation=1.25}}}));
        Write("1_1_1",7);var chest=TutorialObjects.ChestPlacement("1_1_1",7,folder)!;
        Assert.Equal((123,456),(chest.X,chest.Y));Assert.Equal(1.25f,chest.Rotation);
        Write("wrong-area",7);Assert.Null(TutorialObjects.ChestPlacement("1_1_1",7,folder));
        Write("1_1_1",8);Assert.Null(TutorialObjects.ChestPlacement("1_1_1",7,folder));
    }
    [Fact] public void GroundRollbackKeepsTheOriginalClaimId()
    {
        var world=new InstanceState();var item=new WorldItemObject(1,2,Gem);
        uint id=world.PlaceTutorialItem("character/skill",item)!.Value;
        Assert.Null(world.PlaceTutorialItem("character/skill",item));
        world.Ground.Take(id);world.Ground.Restore(id,item);
        Assert.True(world.IsStartWeapon("character/skill",id));Assert.Same(item,world.Ground.Get(id));
    }
    [Fact] public void NamedChestUsesCapturedRoomAndRejectsAnotherAreaOrSeed()
    {
        string file=Path.Combine(folder,"1_1_1_seed1.rooms.json"),path="Metadata/Terrain/Act1/Area1/Rooms/tutorial_01.arm";
        void Write(string area,int seed)=>File.WriteAllText(file,JsonSerializer.Serialize(new {area,seed,rooms=new[]{new {path,x=759,y=483,width=138,height=207,code=0}}}));
        Write("1_1_1",1);
        var room=Assert.Single(TutorialObjects.RoomPlacements("1_1_1",1,path.ToUpperInvariant(),folder));
        var chest=room.Place(new(TutorialObjects.Chest,41,64,5.84685f));
        Assert.Equal((800,547),(chest.X,chest.Y));Assert.Equal(5.84685f,chest.Angle,5);
        // Independently captured Wounded Exile: washed_up3's room code 3 at (207,138), local (98,122).
        var exile=new TutorialObjects.RoomPlacement(207,138,207,207,3).Place(new("exile",98,122));
        Assert.Equal((329,246),(exile.X,exile.Y));
        Write("wrong",1);Assert.Empty(TutorialObjects.RoomPlacements("1_1_1",1,path,folder));
        Write("1_1_1",2);Assert.Empty(TutorialObjects.RoomPlacements("1_1_1",1,path,folder));
    }
    [Fact] public void ActualOpeningZombieResolvesItsVarietyRatherThanItsBaseObjectType()
    {
        if(!File.Exists("data/tables/MonsterVarieties.json")) return;
        var zombie=Assert.IsType<MonsterCatalog.Variety>(new MonsterCatalog().Find(TutorialObjects.Zombie));
        Assert.True(zombie.Supported);
        Assert.Equal("Metadata/Monsters/Zombies/Drowned/BiteZombie",zombie.BaseType);
    }
}

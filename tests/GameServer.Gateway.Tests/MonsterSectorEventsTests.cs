using GameServer.Core.Protocol.Instance;
using Xunit;

// One test for the lineage-gated SectorEvents extra: a Heist rogue ally is creatable, a type outside the
// lineage is not, and allowing SectorEvents writes no bytes.
public sealed class MonsterSectorEventsTests
{
    // The allies' chain runs through NPCAlliesBase and the other type's does not (as the object_types rows give them).
    static readonly string[] AlliesChain =
        ["Metadata/Monsters/Monster", "Metadata/Monsters/LeagueHeist/NPCAllies/NPCAlliesBase"];
    static readonly string[] OtherChain =
        ["Metadata/Monsters/Monster", "Metadata/Monsters/Masters/Kirac"];

    [Fact]
    public void SectorEvents_OnlyHeistNpcAllies()
    {
        MonsterObject.AllowExtraFor("SectorEvents", "Metadata/Monsters/LeagueHeist/NPCAllies/NPCAlliesBase");
        // The allies read the base list plus NPC, MinimapIcon, StateMachine and SectorEvents.
        string[] allies =
            [.. MonsterObject.BaseComponents, "NPC", "MinimapIcon", "StateMachine", "SectorEvents"];
        Assert.True(MonsterObject.CanSerialize(allies, haveStateMachine: true, chain: AlliesChain));
        Assert.False(MonsterObject.CanSerialize(allies, haveStateMachine: true, chain: OtherChain));
        Assert.False(MonsterObject.CanSerialize(allies, haveStateMachine: true));

        // SectorEvents writes nothing: the blob is byte-identical with and without it.
        string[] noStateMachine = [.. MonsterObject.BaseComponents, "NPC", "MinimapIcon", "SectorEvents"];
        var withSector = new MonsterObject(0, 0, 1, noStateMachine) { Chain = AlliesChain };
        var withoutSector = new MonsterObject(0, 0, 1, [.. MonsterObject.BaseComponents, "NPC", "MinimapIcon"]) { Chain = AlliesChain };
        Assert.Equal(withoutSector.Blob(), withSector.Blob());
    }
}

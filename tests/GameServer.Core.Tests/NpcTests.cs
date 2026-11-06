using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class NpcTests
{
    [Fact]
    public void Npc_blob_writes_the_base_npc_components()
    {
        // Positioned as a monster's (team 0), Animated 00, InteractionAction 00, NPC 01 FF (0x141e75050: has dialogue, no
        // component to check), MinimapIcon 00, tasks.
        byte[] blob = new NpcObject(236, 190, NpcObject.BaseComponents).Blob();
        Assert.Equal("0000" + "EC000000" + "BE000000" + "00000000" + "0000" + "00020001" + "00" + "00" + "01FF" + "00" + "00",
            Convert.ToHexString(blob));
    }

    [Fact]
    public void A_hideout_mtx_npc_writes_its_hideout_doodad()
    {
        // The Puppet of Corruption (NPCs 819, 3.29.3c): ControlZone reads nothing, HideoutDoodad is the
        // MtxTypeGameSpecific HASH32 0x721E8B26 and variation 0, StateMachine has its two shared states.
        string[] components = ["Positioned", "Animated", "BaseEvents", "InteractionAction", "NPC", "MinimapIcon",
            "ControlZone", "HideoutDoodad", "StateMachine"];
        var state = new StateMachineData([0, 0], 0);
        Assert.True(NpcObject.CanSerialize(components, state is not null));
        byte[] blob = new NpcObject(416, 679, components, 0, state, 1, null, null, new HideoutDoodad(0x721E8B26, 0)).Blob();
        Assert.Equal(38, blob.Length);
        Assert.Contains("268B1E720000", Convert.ToHexString(blob));
    }

}

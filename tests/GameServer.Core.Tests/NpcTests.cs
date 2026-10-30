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

}

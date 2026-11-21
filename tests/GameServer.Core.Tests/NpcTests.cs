using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class NpcTests
{
    [Fact]
    public void Npc_blob_writes_the_base_npc_components()
    {
        // Positioned as a monster's (team 0), Animated 00, InteractionAction 00, NPC 01 FF (0x141e7b6e0: has dialogue, no
        // component to check), MinimapIcon 00, tasks.
        byte[] blob = new NpcObject(236, 190, NpcObject.BaseComponents).Blob();
        Assert.Equal("0000" + "EC000000" + "BE000000" + "00000000" + "0000" + "00020001" + "00" + "00" + "01FF" + "00" + "00",
            Convert.ToHexString(blob));
    }

    [Fact]
    public void Npc_blob_preserves_overridden_state_machine_flag()
    {
        // Retail StateMachine (0x141e42430): overridden states carry flag 0x02 so the client keeps the server value.
        var states = new StateMachineData([0, 0], 1, ["active", "speaking"]).Set("speaking", 1);
        byte[] plain = new NpcObject(236, 190, NpcObject.BaseComponents).Blob();
        byte[] blob = new NpcObject(236, 190, [.. NpcObject.BaseComponents, "StateMachine"], StateMachine: states).Blob();
        Assert.Equal(plain[..^1], blob[..(plain.Length - 1)]);
        // 2 states, 1 timer; state 0 = (0, flags 0x00); state 1 = (1, flags 0x02); timer 0 = (u32 0, u8 0); tasks 0x00.
        Assert.Equal("0201" + "0000" + "0102" + "0000000000" + "00",
            Convert.ToHexString(blob.AsSpan(plain.Length - 1)));
    }

}

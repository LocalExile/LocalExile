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
    public void Npc_blob_adds_transitionable_and_state_machine()
    {
        string[] components = [.. NpcObject.BaseComponents, "Transitionable", "StateMachine"];
        byte[] blob = new NpcObject(1, 2, components, StateMachine: new([1], 1), TransitionState: 3).Blob();
        Assert.EndsWith("00" + "03" + "0101" + "0100" + "0000000000" + "00", Convert.ToHexString(blob));
    }

    [Fact]
    public void Talk_is_the_world_header_and_a_mode_byte()
    {
        // S→C 0x84 (read 0x141fddcb0): u16 id, u32 BE object id, u32, u16, u8 (0xff opens the dialogue window).
        Assert.Equal("0084" + "00003001" + "00000000" + "0000" + "FF", Convert.ToHexString(InstanceNpcs.Talk(0x3001)));
    }

    [Fact]
    public void Option_is_a_one_byte_message()
    {
        // C→S 0x82: u8 = the NPCTalk row's index among the NPC's rows.
        byte[] m = Convert.FromHexString("008221");
        Assert.Equal(InstanceNpcs.OptionLength, m.Length);
        Assert.Equal(0x21, InstanceNpcs.ReadOption(m));
        Assert.Equal(InstanceNpcs.OptionLength, InstanceProtocol.KnownClientLength(InstanceNpcs.OptionId));
        Assert.Equal(2, InstanceProtocol.KnownClientLength(InstanceNpcs.DialogueClosedId));
    }

    [Fact]
    public void Quest_reward_has_inventory_id_offer_and_flag()
    {
        // S→C 0x87 (read 0x1421a1c00): u32 BE reward inventory id, u16 BE (bit 15 a flag, low 15 bits the value).
        Assert.Equal("0087" + "00000600" + "0008", Convert.ToHexString(InstanceNpcs.OpenQuestReward(0x600, 8)));
        Assert.Equal("0087" + "00000600" + "8008", Convert.ToHexString(InstanceNpcs.OpenQuestReward(0x600, 8, flag: true)));
    }

    [Fact]
    public void Dialogue_has_npc_value_flag_lines_and_choices()
    {
        // S→C 0x8c (read 0x1421a1f60): u32 BE npc, u16 BE, u8, u16 n + n × u32 BE, u16 m + m × (compact int, wstr, u8).
        Assert.Equal("008C" + "00003001" + "0005" + "00" + "0001" + "00000009" + "0000",
            Convert.ToHexString(InstanceNpcs.Dialogue(0x3001, 5, false, [9], [])));
        Assert.Equal("008C" + "00003001" + "0000" + "01" + "0000" + "0001" + "02" + "0002" + "4F4B" + "01",
            Convert.ToHexString(InstanceNpcs.Dialogue(0x3001, 0, true, [], [new(2, "OK")])));
    }

    [Fact]
    public void Npc_types_need_the_npc_component_and_known_ones_only()
    {
        Assert.True(NpcObject.CanSerialize(NpcObject.BaseComponents));
        Assert.False(NpcObject.CanSerialize([.. NpcObject.BaseComponents, "StateMachine"]));
        Assert.False(NpcObject.CanSerialize([.. NpcObject.BaseComponents, "ControlZone"]));
        Assert.False(NpcObject.CanSerialize(["Positioned", "Animated"]));
    }
}

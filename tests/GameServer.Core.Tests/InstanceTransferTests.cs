using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceTransferTests
{
    [Fact]
    public void Exit_click_parses_and_ctrl_is_flag_4()
    {
        // Captured live 2026-10-16: plain and Ctrl+click on the Coast exit (object 0x2000).
        var plain = InstanceTransfer.ReadUseSkillOnObject(Convert.FromHexString("002400002000026610660408"));
        var ctrl = InstanceTransfer.ReadUseSkillOnObject(Convert.FromHexString("0024000020000266108E040C"));
        Assert.Equal((0x2000u, (ushort)0x0266, (ushort)0x1066, false), (plain.TargetId, plain.SkillId, plain.Sequence, plain.Ctrl));
        Assert.True(ctrl.Ctrl);
        Assert.Equal(12, InstanceProtocol.KnownClientLength(InstanceTransfer.UseSkillOnObjectId));
    }

    [Fact]
    public void Change_area_is_hash_and_loading_screen()
    {
        Assert.Equal("02B4" + "7AD2" + "0000", Convert.ToHexString(InstanceTransfer.ChangeArea(0x7ad2)));
    }

    [Fact]
    public void Connect_instance_layout()
    {
        byte[] key = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        byte[] m = InstanceTransfer.ConnectInstance("127.0.0.1", 6112, 0xAABBCCDD, 0x7ad2, key);
        Assert.Equal("0027" + "0009" + Convert.ToHexString("127.0.0.1"u8) + "17E0" + "7F000001" + "00000000" + "7AD2" +
                     "AABBCCDD" + "00" + "00" + Convert.ToHexString(key) + "0000000000000000",
                     Convert.ToHexString(m));
    }

    [Fact]
    public void Instance_list_layout()
    {
        byte[] m = InstanceTransfer.InstanceList(0x7ad2,
            [new(0x0102030405060708, 42, 0, ["Ab"]), new(9, 0, 1, [])]);
        Assert.Equal("00AE" + "7AD2" + "00" + "02" +
                     "0807060504030201" + "0000002A" + "00" + "01" + "0002" + "4162" +   // raw u64 LE, u32 BE, u8, names (wstr)
                     "0900000000000000" + "00000000" + "01" + "00",
                     Convert.ToHexString(m));
    }

    [Fact]
    public void Control_messages_round_trip_as_json()
    {
        var req = new ControlRequest { Op = "transfer", Ticket = 0x1234, Area = 0x7ad2, Instance = 5, New = true };
        Assert.Equal(req, JsonSerializer.Deserialize<ControlRequest>(JsonSerializer.Serialize(req, InstanceControl.Json), InstanceControl.Json));
        var resp = new ControlResponse { Ok = true, Instances = [new(3, 1_792_000_000, 300, ["A"])], Ticket = 0xabc };
        var back = JsonSerializer.Deserialize<ControlResponse>(JsonSerializer.Serialize(resp, InstanceControl.Json), InstanceControl.Json)!;
        Assert.Equal(3ul, back.Instances![0].Id);
        Assert.Equal(["A"], back.Instances[0].Players);
        Assert.Equal((1_792_000_000L, 300, 0xabcu), (back.Instances[0].CreatedUnix, back.Instances[0].SecondsLeft, back.Ticket));
    }
}

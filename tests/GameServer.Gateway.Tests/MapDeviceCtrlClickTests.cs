using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Ctrl + click with the map device open (21-hideouts.md §5): the message a map moves with, and its framing.</summary>
public sealed class MapDeviceCtrlClickTests
{
    // Live 2026-11-15, a map clicked into the slots: 00E4 00000295 00 A1 39 8D 66 2C (12 bytes). The direction is bit 7 of
    // the byte at +9, and the last u16 is the open device object. KnownClientLength must frame both ids, or the click
    // arrives only as an "unframed" run that no handler sees.
    [Fact]
    public void A_ctrl_click_is_framed_and_reads_the_item_the_direction_and_the_device()
    {
        Assert.Equal(InstanceMapDevice.MoveItemLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.MoveIntoDeviceId));
        Assert.Equal(InstanceMapDevice.MoveItemLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.MoveOutOfDeviceId));

        var into = InstanceMapDevice.ReadItemClick(Convert.FromHexString("00E40000029500A1398D662C"));
        Assert.Equal(0x295u, into.ItemId);
        Assert.True(into.IntoDevice);
        Assert.Equal((ushort)0x662C, into.Device);

        // The same item clicked in the slots (0xe5): the direction bit is bit 1 of byte 6, while byte 9 is plain +0x27.
        var back = InstanceMapDevice.ReadItemClick(Convert.FromHexString("00E50000029500A1390D662C"));
        Assert.Equal(0x295u, back.ItemId);
        Assert.False(back.IntoDevice);
        Assert.Equal((byte)0, back.Flags);
        Assert.Equal((byte)0xA1, back.Flags2);
        Assert.Equal((byte)0x39, back.Flags3);
        Assert.Equal((ushort)0x662C, back.Device);
    }

    [Fact]
    public void E4_and_E5_decode_their_distinct_flag_and_direction_layouts()
    {
        // 0x00e4 (writer 0x141f9eac0): byte 6 = (a ? 1 : 0) | (b ? 2 : 0); byte 9 = (into ? 0x80 : 0) | c.
        var e4 = InstanceMapDevice.ReadItemClick(Convert.FromHexString("00E400000123031122854321"));
        Assert.Equal(0x123u, e4.ItemId);
        Assert.Equal((byte)0x03, e4.Flags);
        Assert.Equal((byte)0x11, e4.Flags2);
        Assert.Equal((byte)0x22, e4.Flags3);
        Assert.True(e4.IntoDevice);
        Assert.Equal((ushort)0x4321, e4.Device);

        // 0x00e5 (writer 0x141f9eef0): byte 6 = (a ? 1 : 0) | (into ? 2 : 0) | (b ? 4 : 0); byte 9 = plain c.
        // Byte 9 having bit 7 set (0x85) must NOT flip IntoDevice on 0x00e5 when byte 6 bit 1 is clear!
        var e5Out = InstanceMapDevice.ReadItemClick(Convert.FromHexString("00E500000123051122854321"));
        Assert.Equal(0x123u, e5Out.ItemId);
        Assert.Equal((byte)0x03, e5Out.Flags);   // bit 0 (a) + bit 2 (b) normalized to bits 0..1
        Assert.Equal((byte)0x11, e5Out.Flags2);
        Assert.Equal((byte)0x22, e5Out.Flags3);
        Assert.False(e5Out.IntoDevice);
        Assert.Equal((ushort)0x4321, e5Out.Device);

        // Byte 6 bit 1 set on 0x00e5 sets IntoDevice even when byte 9 bit 7 is clear.
        var e5In = InstanceMapDevice.ReadItemClick(Convert.FromHexString("00E500000123061122054321"));
        Assert.Equal((byte)0x02, e5In.Flags);    // bit 2 (b) normalized to bit 1
        Assert.True(e5In.IntoDevice);
    }
}

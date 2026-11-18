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

        // The same item clicked in the slots (0xe5): the direction bit is clear.
        var back = InstanceMapDevice.ReadItemClick(Convert.FromHexString("00E50000029500A1390D662C"));
        Assert.Equal(0x295u, back.ItemId);
        Assert.False(back.IntoDevice);
        Assert.Equal((ushort)0x662C, back.Device);
    }
}

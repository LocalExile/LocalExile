using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class MapDeviceTests
{
    [Fact]
    public void Activate_is_0xd8_with_a_big_endian_target_and_an_index()
    {
        byte[] m = Convert.FromHexString("00D8012A03");
        Assert.Equal(InstanceMapDevice.ActivateLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.ActivateId));
        Assert.Equal(((ushort)0x012a, (byte)3), InstanceMapDevice.ReadActivate(m));
        var split = InstanceProtocol.SplitClientMessages(Convert.FromHexString("00D8000000"), out var partial, out var unknown);
        Assert.Equal([InstanceMapDevice.ActivateId], split.Select(s => s.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void CtrlClick_is_0xe8_with_the_item_and_the_map_row()
    {
        byte[] m = Convert.FromHexString("00E80000002A0000000D00");
        Assert.Equal(InstanceMapDevice.MoveLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.MoveId));
        var move = InstanceMapDevice.ReadMove(m);
        Assert.Equal(0x2au, move.ItemId);
        Assert.False(move.IntoDevice);
        Assert.Equal(13, move.Row);
        var into = InstanceMapDevice.ReadMove(Convert.FromHexString("00E80000002A0001000D00"));
        Assert.True(into.IntoDevice);
        var split = InstanceProtocol.SplitClientMessages(m, out var partial, out var unknown);
        Assert.Equal([InstanceMapDevice.MoveId], split.Select(s => s.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void VoidstoneClick_is_0xf6_with_the_item()
    {
        byte[] m = Convert.FromHexString("00F60000198D0000");
        Assert.Equal(InstanceMapDevice.SocketLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.SocketId));
        var socket = InstanceMapDevice.ReadSocket(m);
        Assert.Equal(0x198Du, socket.ItemId);
        Assert.Equal(0, socket.A);
        Assert.Equal(0, socket.B);
        var split = InstanceProtocol.SplitClientMessages(m, out var partial, out var unknown);
        Assert.Equal([InstanceMapDevice.SocketId], split.Select(s => s.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>
/// The map device's C→S readers: the Activate (0xd8), the Ctrl + click that moves an item in or out (0xe8) and the
/// Voidstone socket click (0xf6), each with its registered length so the frame splitter can find it.
/// <para>One test because AGENTS.md allows two per PR and the three share a setup.</para>
/// </summary>
public class MapDeviceTests
{
    [Fact]
    public void The_map_devices_client_messages_read_and_frame()
    {
        // 0xd8: a big-endian WorldAreas HASH16 target and an index.
        byte[] activate = Convert.FromHexString("00D8012A03");
        Assert.Equal(InstanceMapDevice.ActivateLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.ActivateId));
        Assert.Equal(((ushort)0x012A, (byte)3), InstanceMapDevice.ReadActivate(activate));
        AssertSplitTo(activate, InstanceMapDevice.ActivateId);

        // 0xe8: the item, whether it goes into the device, and the row (13 is Map1).
        byte[] move = Convert.FromHexString("00E80000002A0000000D00");
        Assert.Equal(InstanceMapDevice.MoveLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.MoveId));
        var outOf = InstanceMapDevice.ReadMove(move);
        Assert.Equal(0x2Au, outOf.ItemId);
        Assert.False(outOf.IntoDevice);
        Assert.Equal(13, outOf.Row);
        Assert.True(InstanceMapDevice.ReadMove(Convert.FromHexString("00E80000002A0001000D00")).IntoDevice);
        AssertSplitTo(move, InstanceMapDevice.MoveId);

        // 0xf6: the Voidstone item clicked into (or out of) a socket.
        byte[] socket = Convert.FromHexString("00F60000198D0000");
        Assert.Equal(InstanceMapDevice.SocketLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.SocketId));
        var read = InstanceMapDevice.ReadSocket(socket);
        Assert.Equal(0x198Du, read.ItemId);
        Assert.Equal(0, read.A);
        Assert.Equal(0, read.B);
        AssertSplitTo(socket, InstanceMapDevice.SocketId);
    }

    /// <summary>The message splits into exactly itself: nothing partial, nothing unknown left over.</summary>
    static void AssertSplitTo(byte[] message, ushort id)
    {
        var split = InstanceProtocol.SplitClientMessages(message, out var partial, out var unknown);
        Assert.Equal([id], split.Select(s => s.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }
}

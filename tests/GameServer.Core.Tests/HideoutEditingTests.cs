using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class HideoutEditingTests
{
    [Fact]
    public void Captured_editor_move_and_following_tick_are_framed_separately()
    {
        byte[] packet = Convert.FromHexString("005B0000300A010B00EA0000000000");
        var move = InstanceHideoutEditing.ReadMove(packet);
        Assert.Equal(0x300au, move.ObjectId);
        Assert.Equal(267, move.X);
        Assert.Equal(234, move.Y);
        Assert.Equal(0, move.Rotation);
        var messages = InstanceProtocol.SplitClientMessages([.. packet, 0, 0x1b, 0, 0, 0, 0, 0], out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(2, messages.Count);
        Assert.Equal(InstanceHideoutEditing.MoveId, messages[0].Id);
        Assert.Equal(InstanceProtocol.TickId, messages[1].Id);
    }

    [Fact]
    public void Rotation_is_network_float_and_component_update_is_little_endian()
    {
        byte[] packet = Convert.FromHexString("005B1234567801020304053FC00000");
        var move = InstanceHideoutEditing.ReadMove(packet);
        Assert.Equal(1.5f, move.Rotation);
        Assert.Equal(2, move.Variation);
        Assert.True(move.Mirrored);
        byte[] update = InstanceHideoutEditing.Rotation(move.ObjectId, move.Rotation);
        Assert.Equal(0x2ba, BinaryPrimitives.ReadUInt16BigEndian(update));
        Assert.Equal("0008000000C03F", Convert.ToHexString(update.AsSpan(update.Length - 7)));
        Assert.Throws<ArgumentException>(() => InstanceHideoutEditing.ReadMove(packet.AsSpan(0, 14)));
    }
}

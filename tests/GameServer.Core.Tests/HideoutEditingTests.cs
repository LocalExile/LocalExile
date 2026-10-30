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

}

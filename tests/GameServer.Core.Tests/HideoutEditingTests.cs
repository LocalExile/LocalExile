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
    public void Hideout_layout_0x0213_roundtrips_frames_and_rejects_excessive_count()
    {
        var layout = new InstanceHideoutEditing.Layout(0xff63, 0xcd42, 0x01,
        [
            new(0x4900c1a6u, 218, 335, 16384, InstanceHideoutEditing.PackFv(5, false)),
            new(0x12345678u, 300, 400, 32768, InstanceHideoutEditing.PackFv(3, true)),
        ]);
        byte[] wire = InstanceHideoutEditing.EncodeLayout(layout);
        Assert.Equal(9 + 2 * 11, wire.Length);
        Assert.Equal(0x0213, BinaryPrimitives.ReadUInt16BigEndian(wire));

        var parsed = InstanceHideoutEditing.ReadLayout(wire);
        Assert.Equal(0xff63, parsed.HideoutHash);
        Assert.Equal(0xcd42, parsed.MusicHash);
        Assert.Equal(1, parsed.Environment);
        Assert.Equal(2, parsed.Doodads.Count);
        Assert.Equal(0x4900c1a6u, parsed.Doodads[0].Hash);
        Assert.Equal((byte)5, parsed.Doodads[0].Variation);
        Assert.False(parsed.Doodads[0].Mirrored);
        Assert.Equal((byte)3, parsed.Doodads[1].Variation);
        Assert.True(parsed.Doodads[1].Mirrored);

        // Partial framing and chaining with following tick
        var partialMsgs = InstanceProtocol.SplitClientMessages(wire[..15], out var partial, out var unknown);
        Assert.Empty(partialMsgs);
        Assert.Equal(15, partial.Length);
        Assert.Empty(unknown);

        var split = InstanceProtocol.SplitClientMessages([.. wire, 0, 0x1b, 0, 0, 0, 0, 0], out partial, out unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(2, split.Count);
        Assert.Equal(InstanceHideoutEditing.LayoutId, split[0].Id);
        Assert.Equal(InstanceProtocol.TickId, split[1].Id);

        // Count above MaxLayoutDoodads is rejected as invalid rather than incomplete
        byte[] oversize = [0x02, 0x13, 0xff, 0x63, 0x00, 0x00, 0x00, 0xff, 0xff];
        var bad = InstanceProtocol.SplitClientMessages(oversize, out partial, out unknown);
        Assert.Empty(bad);
        Assert.Empty(partial);
        Assert.Equal(oversize.Length, unknown.Length);
    }
}

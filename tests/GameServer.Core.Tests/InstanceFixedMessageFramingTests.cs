using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public class InstanceFixedMessageFramingTests
{
    // Field values are fixtures, not inferred semantics. Lengths come from the pinned client writers.
    public static TheoryData<string, ushort, int> FixedMessages => new()
    {
        { "020A01", 0x020a, 3 },
        { "005900150001001B001500000001020A", 0x0059, 16 },
        { "00990100", 0x0099, 4 },
    };

    [Theory]
    [MemberData(nameof(FixedMessages))]
    public void Every_incomplete_prefix_is_carried_until_the_exact_fixed_length(
        string hex, ushort id, int length)
    {
        byte[] packet = Convert.FromHexString(hex);
        Assert.Equal(length, packet.Length);
        Assert.Equal(length, InstanceProtocol.KnownClientLength(id));
        for (int split = 1; split < packet.Length; split++)
        {
            var first = InstanceProtocol.SplitClientMessages(packet.AsSpan(0, split), out var carry, out var unknown);
            Assert.Empty(first);
            Assert.Empty(unknown);
            Assert.Equal(packet[..split], carry);

            var second = InstanceProtocol.SplitClientMessages(
                [.. carry, .. packet[split..]], out carry, out unknown);
            var message = Assert.Single(second);
            Assert.Equal(id, message.Id);
            Assert.Equal(packet, message.Bytes);
            Assert.Empty(carry);
            Assert.Empty(unknown);
        }
    }

    [Theory]
    [MemberData(nameof(FixedMessages))]
    public void Every_split_of_coalesced_tick_fixed_chat_fixed_tick_preserves_message_boundaries(
        string hex, ushort id, int length)
    {
        byte[] packet = Convert.FromHexString(hex);
        byte[] tick = Convert.FromHexString("001B00000000AA");
        const string text = "/goto 1_1_2 framing \u03a9";
        byte[] chat = new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray();
        byte[][] expected = [tick, packet, chat, packet, tick];
        byte[] stream = [.. tick, .. packet, .. chat, .. packet, .. tick];
        Assert.Equal(length, packet.Length);

        // Include unsplit/coalesced input as well as every possible two-read boundary, including inside chat.
        for (int split = 0; split <= stream.Length; split++)
        {
            var messages = InstanceProtocol.SplitClientMessages(stream.AsSpan(0, split), out var carry, out var unknown);
            Assert.Empty(unknown);
            messages.AddRange(InstanceProtocol.SplitClientMessages(
                [.. carry, .. stream[split..]], out carry, out unknown));
            Assert.Empty(carry);
            Assert.Empty(unknown);
            Assert.Equal(new ushort[] { InstanceProtocol.TickId, id, InstanceChat.ChatId, id, InstanceProtocol.TickId },
                messages.Select(m => m.Id));
            for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], messages[i].Bytes);
            Assert.Equal(text, InstanceChat.ReadChat(messages[2].Bytes).Text);
        }
    }

    [Fact]
    public void A_twenty_byte_capture_of_0099_two_ticks_and_01ef_contains_four_messages()
    {
        // Native transfer trace: the previous 20-byte length swallowed these following ticks and 0x01ef.
        byte[] capture = Convert.FromHexString("00990100001B009586263F001B009262EA6C01EF");
        Assert.Equal(20, capture.Length);
        var messages = InstanceProtocol.SplitClientMessages(capture, out var partial, out var unknown);
        Assert.Equal(new ushort[] { 0x0099, InstanceProtocol.TickId, InstanceProtocol.TickId, 0x01ef },
            messages.Select(m => m.Id));
        Assert.Equal(new[] { 4, 7, 7, 2 }, messages.Select(m => m.Bytes.Length));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Both_fixed_messages_survive_one_byte_reads_before_a_wide_string_chat()
    {
        byte[] chat = new LoginWriter().U16(InstanceChat.ChatId).WStr("/goto 1_1_2").U8(0).ToArray();
        byte[] stream = [.. Convert.FromHexString("020A01005900150001001B001500000001020A"), .. chat];
        List<(ushort Id, byte[] Bytes)> messages = [];
        byte[] carry = [];
        foreach (byte value in stream)
        {
            messages.AddRange(InstanceProtocol.SplitClientMessages([.. carry, value], out carry, out var unknown));
            Assert.Empty(unknown);
        }
        Assert.Empty(carry);
        Assert.Equal(new ushort[] { 0x020a, 0x0059, InstanceChat.ChatId }, messages.Select(m => m.Id));
        Assert.Equal("/goto 1_1_2", InstanceChat.ReadChat(messages[2].Bytes).Text);
    }
}

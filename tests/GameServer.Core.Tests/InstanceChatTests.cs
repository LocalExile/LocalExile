using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class InstanceChatTests
{
    [Fact]
    public void Chat_input_is_framed_and_read()
    {
        // Captured live 2026-10-16: "/spawn_mob 42" then "#hello server", in one chunk, followed by a tick fragment.
        byte[] data = Convert.FromHexString("0015000D2F737061776E5F6D6F6220343200" + "0015000D2368656C6C6F2073657276657200" + "001B00");
        var messages = InstanceProtocol.SplitClientMessages(data, out byte[] partial, out byte[] unknown);
        Assert.Equal(2, messages.Count);
        Assert.Equal("001B00", Convert.ToHexString(partial));
        Assert.Empty(unknown);
        var command = InstanceChat.ReadChat(messages[0].Bytes);
        Assert.Equal(("/spawn_mob 42", true), (command.Text, command.IsCommand));
        Assert.False(InstanceChat.ReadChat(messages[1].Bytes).IsCommand);
    }

    [Fact]
    public void A_chat_message_cut_off_waits_for_more_bytes()
    {
        InstanceProtocol.SplitClientMessages(Convert.FromHexString("0015000D2F7370"), out byte[] partial, out _);
        Assert.Equal("0015000D2F7370", Convert.ToHexString(partial));
    }

    [Fact]
    public void Message_box_is_a_wide_string()
    {
        Assert.Equal("000D" + "0002" + "4869", Convert.ToHexString(InstanceChat.MessageBox("Hi")));
    }

    [Fact]
    public void Chat_line_is_the_bytes_the_client_printed()
    {
        // Sent live with /raw 2026-10-20: the chat panel showed "3 Items identified".
        Assert.Equal("0017" + "0000" + "0012" + "33204974656D73206964656E746966696564" + "0000" + "0000" + "00" + "00" +
            "0000000000000000" + "00" + "00", Convert.ToHexString(InstanceChat.ChatLine("3 Items identified")));
    }

    [Fact]
    public void Channel_joins_are_read_from_the_bytes_the_client_sent()
    {
        // Live 2026-10-25: the automatic global join after entering an area, then /global 821 and /trade 822.
        Assert.Equal(new InstanceChat.JoinChannelRequest(ChatChannelKind.Global, InstanceChat.AnyChannel, true, 0),
            InstanceChat.ReadJoinChannel(Convert.FromHexString("0134FFFF000100")));
        Assert.Equal(new InstanceChat.JoinChannelRequest(ChatChannelKind.Global, 821, false, 0),
            InstanceChat.ReadJoinChannel(Convert.FromHexString("01340335000000")));
        Assert.Equal(new InstanceChat.JoinChannelRequest(ChatChannelKind.Trade, 822, false, 0),
            InstanceChat.ReadJoinChannel(Convert.FromHexString("01340336010000")));
        Assert.Null(InstanceChat.ReadJoinChannel(Convert.FromHexString("01340336020000")));
        Assert.Equal(InstanceChat.JoinChannelLength, InstanceProtocol.KnownClientLength(InstanceChat.JoinChannelId));
    }

    [Fact]
    public void Channel_joined_is_the_bytes_the_client_accepted()
    {
        // Sent live with /raw 2026-10-25: the client set global and trade channel 820 and stopped waiting.
        Assert.Equal("013503340000", Convert.ToHexString(InstanceChat.ChannelJoined(ChatChannelKind.Global, 820, 0)));
        Assert.Equal("013503340100", Convert.ToHexString(InstanceChat.ChannelJoined(ChatChannelKind.Trade, 820, 0)));
    }

    [Theory]
    [InlineData("hello", TypedChannel.Local, "hello")]
    public void Typed_lines_are_split_by_their_channel_prefix(string typed, TypedChannel channel, string text) =>
        Assert.Equal((channel, text), InstanceChat.SplitChannel(typed));

    [Fact]
    public void Own_lines_name_the_character_with_the_channel_prefix()
    {
        Assert.Equal("OfflineWitch", InstanceChat.Sender(TypedChannel.Local, "OfflineWitch"));
        Assert.Equal("#OfflineWitch", InstanceChat.Sender(TypedChannel.Global, "OfflineWitch"));
        Assert.Equal("$OfflineWitch", InstanceChat.Sender(TypedChannel.Trade, "OfflineWitch"));
    }

    [Fact]
    public void Stats_update_uses_compact_keys_and_values()
    {
        // Key 244 (0x80F4 as a 2-byte compact) = 100, and a negative value.
        byte[] m = InstanceEntities.StatsUpdate(0x1000, [(244, 100), (5, -1)]);
        Assert.Equal("01B3" + "00001000" + "00000000" + "0000" + "02" + "80F4" + "8064" + "05" + "7F", Convert.ToHexString(m));
    }

    [Theory]
    [InlineData(0x7fu, "7F")]
    public void Compact_uint_matches_the_client_reader(uint value, string hex)
    {
        var w = new GameServer.Core.Protocol.Login.LoginWriter();
        InstanceProtocol.CompactUInt(w, value);
        Assert.Equal(hex, Convert.ToHexString(w.ToArray()));
    }

    [Fact]
    public void Changing_speed_keeps_the_distance_walked()
    {
        var walk = new WalkEstimator(0, 0, 10);
        walk.Order(100, 0, 0);
        Assert.Equal((20, 0), walk.Position(2));
        walk.SetSpeed(20, 2);
        Assert.Equal((20, 0), walk.Position(2));
        Assert.Equal((40, 0), walk.Position(3));
    }
}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChatChannelsTests
{
    [Fact]
    public void A_join_without_a_number_gets_the_default_then_the_last_channel()
    {
        var channels = new ChatChannels();
        Assert.Equal((ChatChannels.DefaultChannel, true), channels.Join("OfflineWitch", ChatChannelKind.Global, InstanceChat.AnyChannel));
        Assert.Equal((ChatChannels.DefaultChannel, false), channels.Join("OfflineWitch", ChatChannelKind.Global, InstanceChat.AnyChannel));

        // /global 821, then the next area's automatic join (0xffff) stays there; trade is separate.
        Assert.Equal(((ushort)821, true), channels.Join("OfflineWitch", ChatChannelKind.Global, 821));
        Assert.Equal(((ushort)821, false), channels.Join("offlinewitch", ChatChannelKind.Global, InstanceChat.AnyChannel));
        Assert.Equal((ChatChannels.DefaultChannel, true), channels.Join("OfflineWitch", ChatChannelKind.Trade, 0));
        Assert.Equal((ChatChannels.DefaultChannel, true), channels.Join("OtherWitch", ChatChannelKind.Global, InstanceChat.AnyChannel));
    }

    [Fact]
    public void The_channels_joined_are_listed_for_the_next_area()
    {
        var channels = new ChatChannels();
        Assert.Empty(channels.Current("OfflineWitch"));
        channels.Join("OfflineWitch", ChatChannelKind.Trade, 822, 3);
        channels.Join("OfflineWitch", ChatChannelKind.Global, InstanceChat.AnyChannel);
        Assert.Equal([(ChatChannelKind.Global, ChatChannels.DefaultChannel, (byte)0), (ChatChannelKind.Trade, (ushort)822, (byte)3)],
            channels.Current("OFFLINEWITCH"));
    }

    [Fact]
    public void The_joined_line_names_the_channel() =>
        Assert.Equal("You have joined trade chat channel 822.", ChatChannels.JoinedText(ChatChannelKind.Trade, 822));

    [Theory]
    [InlineData("hello", "OfflineWitch", "hello")]
    public void A_typed_line_comes_back_from_the_character_on_its_channel(string typed, string sender, string text) =>
        Assert.Equal(InstanceChat.ChatLine(text, sender), ChatChannels.Echo("OfflineWitch", typed));

    [Theory]
    [InlineData("")]
    public void Empty_party_guild_and_whisper_lines_are_not_echoed(string typed) =>
        Assert.Null(ChatChannels.Echo("OfflineWitch", typed));
}

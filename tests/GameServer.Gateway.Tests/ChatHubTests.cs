using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChatHubTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static (ChatMember Member, List<ChatFrame> Heard) Enter(ChatHub hub, string name)
    {
        var heard = new List<ChatFrame>();
        return (hub.Enter(name, heard.Add)!, heard);
    }

    [Fact]
    public void Lines_reach_their_channel_and_whispers_their_character()
    {
        var hub = new ChatHub();
        var (ann, annHeard) = Enter(hub, "Ann");
        var (bob, bobHeard) = Enter(hub, "Bob");
        var (cid, cidHeard) = Enter(hub, "Cid");
        var (twin, twinHeard) = Enter(hub, "ANN");
        Assert.Equal("ANN[1]", twin.Name);
        Assert.Equal("Ann[2]", hub.Enter("Ann", _ => { })!.Name);
        Assert.Null(hub.Enter("#Dan", _ => { }));
        Assert.Null(hub.Enter("Dan[1]", _ => { }));
        hub.Handle(bob, new ChatFrame(ChatFrame.Whisper, "Ann[1]", Text: "which?"));
        Assert.Equal([new ChatFrame(ChatFrame.Whisper, "Bob", Text: "which?")], twinHeard);
        bobHeard.Clear();
        hub.Handle(ann, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Global, Channel: 1));
        hub.Handle(bob, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Global, Channel: 1));
        hub.Handle(cid, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Global, Channel: 2));

        hub.Handle(ann, new ChatFrame(ChatFrame.Say, Kind: ChatChannelKind.Global, Text: "hi\u0007"));
        var line = new ChatFrame(ChatFrame.Line, "Ann", ChatChannelKind.Global, Text: "hi");
        Assert.Equal([line], annHeard);
        Assert.Equal([line], bobHeard);
        Assert.Empty(cidHeard);

        // A new channel of the kind replaces the old one, and a member's channels end with it.
        hub.Handle(bob, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Global, Channel: 2));
        hub.Leave(cid);
        hub.Handle(bob, new ChatFrame(ChatFrame.Say, Kind: ChatChannelKind.Global, Text: "there"));
        Assert.Single(annHeard);

        // One number in two languages is two channels.
        hub.Handle(bob, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Global, Channel: 1, Language: 2));
        hub.Handle(bob, new ChatFrame(ChatFrame.Say, Kind: ChatChannelKind.Global, Text: "privet"));
        Assert.Single(annHeard);
        Assert.Empty(cidHeard);

        annHeard.Clear();
        bobHeard.Clear();
        hub.Handle(ann, new ChatFrame(ChatFrame.Whisper, "bob", Text: "psst"));
        hub.Handle(ann, new ChatFrame(ChatFrame.Whisper, "Cid", Text: "gone?"));
        Assert.Equal([new ChatFrame(ChatFrame.Whisper, "Ann", Text: "psst")], bobHeard);
        Assert.Equal([new ChatFrame(ChatFrame.Whispered, "Bob", Text: "psst"), new ChatFrame(ChatFrame.Offline, "Cid")], annHeard);
    }

    [Fact]
    public async Task A_flood_is_held_back_and_a_frame_over_the_limit_is_refused()
    {
        var clock = new Clock();
        var hub = new ChatHub(clock);
        var (ann, heard) = Enter(hub, "Ann");
        hub.Handle(ann, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Trade, Channel: 1));
        var say = new ChatFrame(ChatFrame.Say, Kind: ChatChannelKind.Trade, Text: "wts");

        for (int i = 0; i <= ChatHub.Burst; i++) hub.Handle(ann, say);
        Assert.Equal(ChatHub.Burst, heard.Count(f => f.Type == ChatFrame.Line));
        Assert.Equal(ChatFrame.TooFast, heard[^1].Type);
        clock.Now += TimeSpan.FromSeconds(1);
        hub.Handle(ann, say);
        Assert.Equal(ChatFrame.Line, heard[^1].Type);

        using var wire = new MemoryStream();
        await ChatWire.WriteAsync(wire, say, default);
        wire.Position = 0;
        Assert.Equal(say, await ChatWire.ReadAsync(wire, default));
        Assert.Null(await ChatWire.ReadAsync(wire, default));
        using var tooLong = new MemoryStream([0xff, 0xff]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ChatWire.ReadAsync(tooLong, default));
    }
}

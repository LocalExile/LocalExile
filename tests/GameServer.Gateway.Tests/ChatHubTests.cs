using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

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
        Assert.Equal("ANN_1", twin.Name);
        Assert.Equal("Ann_2", hub.Enter("Ann", _ => { })!.Name);
        Assert.Null(hub.Enter("#Dan", _ => { }));
        Assert.Null(hub.Enter("Dan[1]", _ => { }));
        hub.Handle(bob, new ChatFrame(ChatFrame.Whisper, "Ann_1", Text: "which?"));
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

    [Fact]
    public void A_linked_item_is_framed_and_reaches_the_line()
    {
        // As captured live 2026-11-07: the link, then "#_" with the mark's place, then a tick.
        byte[] link = Convert.FromHexString("001200000000070000001700");
        byte[] typed = new LoginWriter().U16(InstanceChat.ChatId).WStr("#_").U8(1).U32(1).ToArray();
        var messages = InstanceProtocol.SplitClientMessages([.. link, .. typed, .. Convert.FromHexString("020A01")], out _, out byte[] unknown);
        Assert.Empty(unknown);
        Assert.Equal(new ushort[] { InstanceChat.LinkItemId, InstanceChat.ChatId, 0x020a }, messages.Select(m => m.Id));
        Assert.Equal(new InstanceChat.LinkItem(0, 7, 0x17), InstanceChat.ReadLinkItem(messages[0].Bytes));
        Assert.Equal([1u], InstanceChat.ReadChat(messages[1].Bytes).Links);

        Assert.EndsWith("00" + "01" + "00000002" + "AABB", Convert.ToHexString(InstanceChat.ChatLine("a _", items: [new ChatLineItem(2, [0xaa, 0xbb])])));

        // A chat server passes on the items that stand on a mark, in the text's order, and gives up on those too long for a frame.
        var hub = new ChatHub();
        var (ann, heard) = Enter(hub, "Ann");
        hub.Handle(ann, new ChatFrame(ChatFrame.Join, Kind: ChatChannelKind.Global, Channel: 1));
        hub.Handle(ann, new ChatFrame(ChatFrame.Say, Kind: ChatChannelKind.Global, Text: "_ a _",
            Items: [new ChatFrameItem(4, "second"), new ChatFrameItem(2, "off the mark"), new ChatFrameItem(0, "first"), new ChatFrameItem(9, "past the end")]));
        Assert.Equal(["first", "second"], heard[^1].Items!.Select(i => i.Item));
        hub.Handle(ann, new ChatFrame(ChatFrame.Say, Kind: ChatChannelKind.Global, Text: "_", Items: [new ChatFrameItem(0, new string('x', ChatWire.MaxFrame))]));
        Assert.Equal("_", heard[^1].Text);
        Assert.Null(heard[^1].Items);
    }

    [Fact]
    public void A_linked_item_from_a_chat_server_is_built_from_the_game_data()
    {
        var catalog = new ItemCatalog("data");
        var item = ItemCatalog.Create(catalog.Find("Metadata/Items/Armours/BodyArmours/BodyDexInt1")!);
        string written = ChatItems.Write(item, catalog);
        Assert.Equal(item.Wire(), ChatItems.Wire(written, catalog));

        var saved = CharacterSaves.ToSaved(item, catalog.Mods);
        string Forged(CharacterSaves.SavedItem forged) => System.Text.Json.JsonSerializer.Serialize(forged, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Null(ChatItems.Wire("not an item", catalog));
        Assert.Null(ChatItems.Wire(Forged(saved with { Type = 0xdeadbeef }), catalog));
        Assert.Null(ChatItems.Wire(Forged(saved with { Mods = [new("NoSuchMod", ModSection.Explicit, [1])] }), catalog));
        Assert.Null(ChatItems.Wire(Forged(saved with { Sockets = [.. Enumerable.Repeat(new CharacterSaves.SavedSocket(SocketColour.Red), 7)] }), catalog));
        Assert.Null(ChatItems.Wire(Forged(saved with { Sockets = [new(SocketColour.Red)], Links = [2] }), catalog));
        Assert.Equal(item.Wire(), ChatItems.Wire(Forged(saved with { Visual = 0xffff, Name = [0xdeadbeef], AlternateQualityType = 200, GemVariant = 99 }), catalog));
    }
}

using System.Collections.Concurrent;
using System.Net;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Chat;

/// <summary>What the player types, on its way to the others: local lines to the players of the instance, global, trade and
/// whisper lines through a chat server.</summary>
public sealed class ChatModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var chat = new ChatService(module.Log);
        module.Lifetime.Own(chat);
        module.Lifetime.Own(ChatRoutes.Follow(chat.RouteChanged));
        GameEvents.Packet(InstanceChat.LinkItemId).Add(module.Manifest.Id, (conn, bytes) => chat.Linked(conn, InstanceChat.ReadLinkItem(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.ChatId).Add(module.Manifest.Id, (conn, bytes) => chat.Typed(conn, InstanceChat.ReadChat(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.JoinChannelId).Add(module.Manifest.Id, (conn, bytes) => chat.Join(conn, bytes));
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn => chat.Entered(conn));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) => chat.Leaving(conn, why));
        _ = chat.Rejoin();
    }
}

/// <summary>The local chat server and the session of each character on it.</summary>
sealed class ChatService : IDisposable
{
    readonly ChatServer _local = new(IPAddress.Loopback, 0);
    readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<ClientState, ConcurrentDictionary<byte, Item>> _linked = new();

    public ChatChannels Channels { get; } = new();
    public Action<string> Log { get; }
    /// <summary>The chat server a mod chose, or null for the local one alone.</summary>
    public (string Host, int Port)? Remote => ChatRoutes.Current;
    public int LocalPort => _local.Port;

    public ChatService(Action<string> log)
    {
        Log = log;
        _local.Start();
        log($"local chat server on 127.0.0.1:{LocalPort}" + (Remote is var (host, port) ? $", remote chat server {host}:{port}" : ""));
    }

    public void RouteChanged()
    {
        foreach (var session in _sessions.Values) session.RouteChanged();
    }

    ChatSession Session(ClientState conn)
    {
        string name = CharacterName(conn);
        if (_sessions.TryGetValue(name, out var open)) return open;
        var session = new ChatSession(this, name, conn);
        if (!_sessions.TryAdd(name, session)) return _sessions[name];
        session.Open();
        return session;
    }

    // C→S 0x12: an item of the line the next 0x15 brings. Read here, so the line shows it as it is now.
    public Task<bool> Linked(ClientState conn, InstanceChat.LinkItem link)
    {
        var stash = StashOf(conn);
        IReadOnlyList<InventoryItem> items = stash.TabOf(link.InventoryId) is int tab ? stash.Inventory(tab).Items
            : PlayerInventories.Row(link.InventoryId) is byte row ? ItemsOf(conn).Items(row) : [];
        if (items.FirstOrDefault(i => i.Id == link.ItemId) is { } linked) _linked.GetOrAdd(conn, _ => new())[link.Index] = linked.Item;
        else Log($"{conn.Wire.Remote} <= 0x0012 link of {link}: no such item");
        return Task.FromResult(true);
    }

    // The linked items of a line, where they stand in its text: cut is what the text lost from the front of the typed line.
    static IReadOnlyList<(int At, Item Item)> Placed(InstanceChat.ChatInput chat, ConcurrentDictionary<byte, Item>? linked, string text, int cut) =>
        linked is null ? [] : [.. chat.Links.Select((link, index) => (At: (int)link - cut, Item: linked.GetValueOrDefault((byte)index)))
            .Where(l => l.Item is not null && l.At >= 0 && l.At < text.Length && text[l.At] == ChatWire.ItemMark).Select(l => (l.At, l.Item!))];

    static IReadOnlyList<ChatFrameItem>? FrameItems(ClientState conn, IReadOnlyList<(int At, Item Item)> placed, string text) =>
        ChatWire.Items(text, [.. placed.Select(l => new ChatFrameItem(l.At, ChatItems.Write(l.Item, ItemsFor(conn))))]);

    // The items of the sender's own line, as the 0x17 it comes back with carries them: in the order of the text, up to
    // the limit the client reads.
    static IReadOnlyList<ChatLineItem> OwnLine(InstanceChat.ChatInput chat, ConcurrentDictionary<byte, Item>? linked, string text, int cut) =>
        [.. Placed(chat, linked, text, cut).OrderBy(l => l.At).Take(ChatWire.MaxItems).Select(l => new ChatLineItem((uint)l.At, l.Item.Wire()))];

    /// <summary>The items of a line from a chat server that this client can be shown: the others stay '_'.</summary>
    public IReadOnlyList<ChatLineItem>? LineItems(ClientState conn, string text, IReadOnlyList<ChatFrameItem>? items)
    {
        if (ChatWire.Items(text, items) is not { } placed) return null;
        var catalog = ItemsFor(conn);
        return [.. placed.Select(i => (i.At, Wire: ChatItems.Wire(i.Item, catalog))).Where(i => i.Wire is not null).Select(i => new ChatLineItem((uint)i.At, i.Wire!))];
    }

    // C→S 0x15 without a slash: a slash command is the Gateway's. The sender sees its own line as the server sends it back.
    public async Task<bool> Typed(ClientState conn, InstanceChat.ChatInput chat)
    {
        _linked.TryRemove(conn, out var linked);
        if (chat.IsCommand) return false;
        string name = CharacterName(conn);
        Log($"{conn.Wire.Remote} <= 0x0015 chat \"{chat.Text}\"");
        var (channel, typed) = InstanceChat.SplitChannel(chat.Text);
        string text = ChatWire.Clean(typed);
        if (string.IsNullOrWhiteSpace(text)) return true;
        int prefix = chat.Text.Length - typed.Length;
        switch (channel)
        {
            case TypedChannel.Local:
                await SayLocally(conn, text, name, OwnLine(chat, linked, text, prefix));
                break;
            case TypedChannel.Party:
                // The party is a module's to know, so the framework hands the line over; nothing else keeps it.
                if (!await GameEvents.RunPartyChat(conn, text, OwnLine(chat, linked, text, prefix)))
                    Log($"{conn.Wire.Remote} <= 0x0015 party line \"{text}\" from {name} with no party to take it");
                break;
            case TypedChannel.Global or TypedChannel.Trade:
                var kind = channel == TypedChannel.Global ? ChatChannelKind.Global : ChatChannelKind.Trade;
                var session = Session(conn);
                await session.Ready;
                session.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Say, Kind: kind, Text: text, Items: FrameItems(conn, Placed(chat, linked, text, prefix), text))));
                break;
            case TypedChannel.Whisper:
                // "@Name the text".
                int space = text.IndexOf(' ');
                if (space <= 0 || text[(space + 1)..].Trim() is not { Length: > 0 } whisper) break;
                int start = text.Length - text[(space + 1)..].TrimStart().Length;
                var whispering = Session(conn);
                await whispering.Ready;
                whispering.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Whisper, text[..space], Text: whisper,
                    Items: FrameItems(conn, Placed(chat, linked, whisper, prefix + start), whisper))));
                break;
        }
        return true;
    }

    // Not through a chat server: who is in the area is this Gateway's to know. Each gets its own bytes, as a send encrypts
    // them in place: a second client given the same array read noise and crashed (live 2026-11-07).
    static async Task SayLocally(ClientState conn, string text, string name, IReadOnlyList<ChatLineItem> items)
    {
        foreach (var other in InGame.Keys)
            if (other is { Controlled: false, Closed: false, Outbox: { } outbox } && ReferenceEquals(other.Instance.Presence, conn.Instance.Presence))
            {
                try { await outbox(InstanceChat.ChatLine(text, name, items: items), $"0x0017 chat line from {name}"); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
    }

    (ushort Channel, bool Changed) JoinOn(ChatSession session, string name, ChatChannelKind kind, ushort requested, byte language)
    {
        var joined = Channels.Join(name, kind, requested, language);
        session.Send(new ChatFrame(ChatFrame.Join, Kind: kind, Channel: joined.Channel, Language: language));
        return joined;
    }

    // C→S 0x134: the client joins a global or trade channel after entering an area and for /global N and /trade N, and
    // prints "Timeout while joining … chat channel" when no 0x135 comes within 5 s. The join is answered with 0x135 at once,
    // whatever the chat server is doing, then a chat line naming the channel when it is new for the character or was asked
    // for by a command.
    public async Task<bool> Join(ClientState conn, ReadOnlyMemory<byte> bytes)
    {
        string remote = conn.Wire.Remote;
        if (InstanceChat.ReadJoinChannel(bytes.Span) is not { } join)
        {
            Log($"{remote} <= 0x0134 join with an unknown channel kind, ignored: {Convert.ToHexString(bytes.Span)}");
            return true;
        }
        string asked = join.Channel == InstanceChat.AnyChannel ? "any" : join.Channel.ToString();
        Log($"{remote} <= 0x0134 join {join.Kind} chat channel {asked} ({(join.Automatic ? "on entering" : "command")}, language {join.Language})");
        var (channel, changed) = JoinOn(Session(conn), CharacterName(conn), join.Kind, join.Channel, join.Language);
        await Send(remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChannelJoined(join.Kind, channel, join.Language), $"0x0135 joined {join.Kind} chat channel {channel}");
        if (changed || !join.Automatic)
        {
            string text = ChatChannels.JoinedText(join.Kind, channel, join.Language);
            await Send(remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        }
        return true;
    }

    // Every area: the channels the character joined earlier in the session (S→C 0x135), since the new instance connection
    // starts without any and the client asks only once per session.
    public async Task Entered(ClientState conn)
    {
        var channels = Channels.Current(CharacterName(conn));
        if (channels.Count == 0) return;
        Session(conn).Entered(conn);
        foreach (var (kind, channel, language) in channels)
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChannelJoined(kind, channel, language), $"0x0135 still on {kind} chat channel {channel}");
    }

    // A load while players are in game (a changed setting, a new build) knows no channels, and the clients do not join
    // again: they go back to the default ones, on the servers of this load.
    public async Task Rejoin()
    {
        foreach (var conn in InGame.Keys)
        {
            if (conn is not { Controlled: false, Closed: false, PlayerSent: true }) continue;
            var session = Session(conn);
            foreach (var kind in (ChatChannelKind[])[ChatChannelKind.Global, ChatChannelKind.Trade])
                JoinOn(session, CharacterName(conn), kind, InstanceChat.AnyChannel, 0);
            try { await Entered(conn); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    // An area change closes the connection too, and the next one takes the session over.
    public Task Leaving(ClientState conn, string why)
    {
        _linked.TryRemove(conn, out _);
        if (why == "disconnected" && !conn.LeavingArea && conn.PendingTransfer is null
            && _sessions.TryGetValue(CharacterName(conn), out var session) && ReferenceEquals(session.Conn, conn)
            && _sessions.TryRemove(CharacterName(conn), out _))
            session.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _local.Dispose();
    }
}

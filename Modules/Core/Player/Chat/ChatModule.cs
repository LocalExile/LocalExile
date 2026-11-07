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
        module.OnPacket(InstanceChat.ChatId, p => chat.Typed(ConnOf(p.Player), InstanceChat.ReadChat(p.Bytes.Span)));
        module.OnPacket(InstanceChat.JoinChannelId, p => chat.Join(ConnOf(p.Player), p.Bytes));
        module.OnAreaGenerated(player => chat.Entered(ConnOf(player)));
        module.OnPlayerLeaving((player, why) => chat.Leaving(ConnOf(player), why));
        _ = chat.Rejoin();
    }
}

/// <summary>The local chat server and the session of each character on it.</summary>
sealed class ChatService : IDisposable
{
    readonly ChatServer _local = new(IPAddress.Loopback, 0);
    readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

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

    // C→S 0x15 without a slash: a slash command is the Gateway's. The sender sees its own line as the server sends it back.
    public async Task<bool> Typed(ClientState conn, InstanceChat.ChatInput chat)
    {
        if (chat.IsCommand) return false;
        string name = CharacterName(conn);
        Log($"{conn.Wire.Remote} <= 0x0015 chat \"{chat.Text}\"");
        var (channel, typed) = InstanceChat.SplitChannel(chat.Text);
        string text = ChatWire.Clean(typed);
        if (string.IsNullOrWhiteSpace(text)) return true;
        switch (channel)
        {
            case TypedChannel.Local:
                await SayLocally(conn, text, name);
                break;
            case TypedChannel.Global or TypedChannel.Trade:
                var kind = channel == TypedChannel.Global ? ChatChannelKind.Global : ChatChannelKind.Trade;
                var session = Session(conn);
                await session.Ready;
                session.Send(new ChatFrame(ChatFrame.Say, Kind: kind, Text: text));
                break;
            case TypedChannel.Whisper:
                // "@Name the text".
                int space = text.IndexOf(' ');
                if (space <= 0 || text[(space + 1)..].Trim() is not { Length: > 0 } whisper) break;
                var whispering = Session(conn);
                await whispering.Ready;
                whispering.Send(new ChatFrame(ChatFrame.Whisper, text[..space], Text: whisper));
                break;
        }
        return true;
    }

    // Not through a chat server: who is in the area is this Gateway's to know. Each gets its own bytes, as a send encrypts
    // them in place: a second client given the same array read noise and crashed (live 2026-11-07).
    static async Task SayLocally(ClientState conn, string text, string name)
    {
        foreach (var other in InGame.Keys)
            if (other is { Controlled: false, Closed: false, Outbox: { } outbox } && ReferenceEquals(other.Instance.Presence, conn.Instance.Presence))
            {
                try { await outbox(InstanceChat.ChatLine(text, name), $"0x0017 chat line from {name}"); }
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

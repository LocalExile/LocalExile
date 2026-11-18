using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Chat;

/// <summary>A character's place in chat, from its first channel until it leaves the game: it outlives the connection of each
/// area. It keeps one connection to the local server and one to each remote server used by its rooms or whispers.</summary>
sealed class ChatSession(ChatService chat, string name, ClientState conn) : IDisposable
{
    /// <summary>The time between tries of a remote server that failed, each at an area change.</summary>
    const long RetryMilliseconds = 60_000;

    readonly Lock _gate = new();
    readonly CancellationTokenSource _stop = new();
    sealed class RemoteConnection((string Host, int Port) endpoint)
    {
        public readonly (string Host, int Port) Endpoint = endpoint;
        public ChatLink? Link;
        public long NextTry;
        public bool Connecting;
    }

    readonly Dictionary<(string Host, int Port), RemoteConnection> _remotes = [];
    ChatLink? _local;
    ChatRoutes.RemoteRoute? _route;
    bool _disposed;

    public ClientState Conn { get; private set; } = conn;

    /// <summary>Done when the local chat server has the character, so a typed line has somewhere to go.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Open()
    {
        Ready = OpenLocal();
        RouteChanged();
    }

    public void Entered(ClientState entered)
    {
        Conn = entered;
        lock (_gate) if (!_disposed) RefreshConnections();
    }

    public void RouteChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _route = ChatRoutes.Selection;
            RefreshConnections();
            SyncRooms();
        }
    }

    public void Send(ChatFrame frame)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (frame is { Type: ChatFrame.Join, Kind: { } kind, Channel: { } channel })
            {
                RefreshConnections();
                Join(kind, channel, frame.Language ?? 0);
            }
            else if (frame.Kind is { } roomKind && chat.Channels.Find(name, roomKind) is { } room)
                LinkFor(roomKind, room.Channel)?.Send(frame);
            else if (frame.Type == ChatFrame.Whisper)
                LinkAt(_route?.WhisperServer)?.Send(frame);
        }
    }

    ChatLink? LinkAt((string Host, int Port)? endpoint) =>
        endpoint is { } server && _remotes.TryGetValue(server, out var remote) && remote.Link is { } link ? link : _local;

    ChatLink? LinkFor(ChatChannelKind kind, ushort channel) => LinkAt(_route?.ServerFor(kind, channel));

    void RefreshConnections()
    {
        HashSet<(string Host, int Port)> wanted = [];
        if (_route is { } route)
        {
            if (route.WhisperServer is { } whisperServer) wanted.Add(whisperServer);
            foreach (var (kind, channel, _) in chat.Channels.Current(name))
                if (route.ServerFor(kind, channel) is { } server) wanted.Add(server);
        }

        foreach (var (server, remote) in _remotes)
            if (!wanted.Contains(server))
            {
                _remotes.Remove(server);
                remote.Link?.Dispose();
            }

        foreach (var server in wanted)
        {
            if (!_remotes.TryGetValue(server, out var remote)) _remotes.Add(server, remote = new(server));
            if (remote.Link is null && !remote.Connecting && Environment.TickCount64 >= remote.NextTry)
            {
                remote.Connecting = true;
                _ = OpenRemote(remote);
            }
        }
    }

    void SyncRooms()
    {
        foreach (var (kind, channel, language) in chat.Channels.Current(name)) Join(kind, channel, language);
    }

    void Join(ChatChannelKind kind, ushort channel, byte language)
    {
        var chosen = LinkFor(kind, channel);
        var leave = new ChatFrame(ChatFrame.LeaveChannel, Kind: kind);
        chosen?.Send(new ChatFrame(ChatFrame.Join, Kind: kind, Channel: channel, Language: language));
        if (_local != chosen) _local?.Send(leave);
        foreach (var remote in _remotes.Values)
            if (remote.Link != chosen) remote.Link?.Send(leave);
    }

    async Task OpenLocal()
    {
        try
        {
            var link = await Connect("127.0.0.1", chat.LocalPort);
            lock (_gate)
            {
                if (_disposed || link.IsClosed) { link.Dispose(); return; }
                _local = link;
                SyncRooms();
            }
        }
        catch (IOException e) { if (!_stop.IsCancellationRequested) chat.Log($"{name}: the local chat server refused: {e.Message}"); }
    }

    async Task OpenRemote(RemoteConnection remote)
    {
        var (host, port) = remote.Endpoint;
        try
        {
            var link = await Connect(host, port);
            lock (_gate)
            {
                if (_disposed || _remotes.GetValueOrDefault(remote.Endpoint) != remote || link.IsClosed)
                {
                    link.Dispose();
                    return;
                }
                remote.Link = link;
                remote.NextTry = 0;
                SyncRooms();
            }
            chat.Log($"{name}: on chat server {host}:{port}");
            await Tell("Connected to the online chat server." + (link.Name == name ? "" : $" Another player is named {name} there: you are {link.Name}."));
        }
        catch (IOException e)
        {
            bool report;
            lock (_gate)
            {
                report = !_disposed && _remotes.GetValueOrDefault(remote.Endpoint) == remote;
                if (report) remote.NextTry = Environment.TickCount64 + RetryMilliseconds;
            }
            if (report)
            {
                chat.Log($"{name}: chat server {host}:{port}: {e.Message}");
                await Tell($"Failed to connect to {host}:{port} chat channel, switching back to local chat server.");
            }
        }
        finally
        {
            lock (_gate) remote.Connecting = false;
        }
    }

    Task<ChatLink> Connect(string host, int port) => ChatLink.ConnectAsync(host, port, name, Received, Closed, _stop.Token);

    void Closed(ChatLink link)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (ReferenceEquals(link, _local))
            {
                _local = null;
                chat.Log($"{name}: lost the local chat server");
                return;
            }
            var remote = _remotes.Values.FirstOrDefault(remote => remote.Link == link);
            if (remote is null) return;
            remote.Link = null;
            remote.NextTry = Environment.TickCount64 + RetryMilliseconds;
            SyncRooms();
        }
        _ = Tell("Lost an online chat server. Its channels will use local chat.");
    }

    /// <summary>A frame from the chat server: untrusted like all of it, so a name that could pose as a prefix is dropped.</summary>
    Task Received(ChatLink source, ChatFrame frame)
    {
        lock (_gate)
        {
            if (_disposed || (source != _local && !_remotes.Values.Any(remote => remote.Link == source))) return Task.CompletedTask;
            if (frame.Type == ChatFrame.Line && frame.Kind is { } kind)
            {
                if (chat.Channels.Find(name, kind) is not { } room || LinkFor(kind, room.Channel) != source
                    || (frame.Channel is { } channel && channel != room.Channel)
                    || (frame.Language is { } language && language != room.Language)) return Task.CompletedTask;
            }
            else if (frame.Type is ChatFrame.Whisper or ChatFrame.Whispered or ChatFrame.Offline
                && source != LinkAt(_route?.WhisperServer)) return Task.CompletedTask;
        }
        frame = ChatWire.CleanLine(frame);
        string text = frame.Text!;
        bool named = ChatWire.ValidMember(frame.Name);
        if (GameEvents.ChatPosted.Any && !Conn.Controlled
            && frame is { Type: ChatFrame.Line, Kind: ChatChannelKind.Global or ChatChannelKind.Trade, Channel: not null, Language: not null }
            && named && frame.Name == source.Name && text.Length > 0)
            foreach (var (owner, posted) in GameEvents.ChatPosted.Subscribers(Conn))
                GameEventOwners.Guard(owner, nameof(GameEvents.ChatPosted), () => posted(Conn, frame));
        return frame.Type switch
        {
            ChatFrame.Line when named && frame.Kind is ChatChannelKind.Global or ChatChannelKind.Trade && text.Length > 0 =>
                Deliver(InstanceChat.ChatLine(text, InstanceChat.Sender(frame.Kind == ChatChannelKind.Global ? TypedChannel.Global : TypedChannel.Trade, frame.Name!),
                    items: chat.LineItems(Conn, text, frame.Items)),
                    $"0x0017 {frame.Kind} chat line from {frame.Name}"),
            ChatFrame.Whisper when named && text.Length > 0 =>
                Deliver(InstanceChat.ChatLine(text, "@" + frame.Name, items: chat.LineItems(Conn, text, frame.Items)), $"0x0017 whisper from {frame.Name}"),
            ChatFrame.Whispered when named && text.Length > 0 =>
                Deliver(InstanceChat.ChatLine(text, "@" + frame.Name, InstanceChat.WhisperSentFlag, chat.LineItems(Conn, text, frame.Items)), $"0x0017 whisper to {frame.Name}"),
            ChatFrame.Offline when named => Tell($"{frame.Name} is not online."),
            ChatFrame.TooFast => Tell("You are sending messages too fast."),
            _ => Task.CompletedTask,
        };
    }

    Task Tell(string text) => Deliver(InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");

    async Task Deliver(byte[] line, string log)
    {
        // Between two areas nobody is there to see it.
        if (Conn is not { Closed: false, Outbox: { } outbox }) return;
        try { await outbox(line, log); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _local?.Dispose();
            foreach (var remote in _remotes.Values) remote.Link?.Dispose();
            _remotes.Clear();
            _local = null;
        }
    }
}

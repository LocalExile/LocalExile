using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Chat;

/// <summary>A character's place in chat, from its first channel until it leaves the game: it outlives the connection of each
/// area. It is on the local chat server, or on the remote one a mod chose while that can be reached.</summary>
sealed class ChatSession(ChatService chat, string name, ClientState conn) : IDisposable
{
    /// <summary>The time between tries of a remote server that failed, each at an area change.</summary>
    const long RetryMilliseconds = 60_000;

    readonly Lock _gate = new();
    readonly CancellationTokenSource _stop = new();
    ChatLink? _link;
    bool _onRemote, _disposed;
    string? _told;
    long _nextRemoteTry;
    int _trying;

    public ClientState Conn { get; private set; } = conn;

    /// <summary>Done when the local chat server has the character, so a typed line has somewhere to go.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Open()
    {
        Ready = UseLocalAsync();
        if (chat.Remote is not null) _ = Ready.ContinueWith(_ => TryRemoteAsync(), TaskScheduler.Default);
    }

    /// <summary>The character's connection in the area it entered, and another try of a remote server that failed.</summary>
    public void Entered(ClientState entered)
    {
        Conn = entered;
        bool retry;
        lock (_gate) retry = chat.Remote is not null && !_onRemote && !_disposed && Environment.TickCount64 >= _nextRemoteTry;
        if (retry) _ = TryRemoteAsync();
    }

    /// <summary>A mod chose a chat server, or stopped: the character moves now, not at its next area.</summary>
    public void RouteChanged()
    {
        bool toLocal;
        lock (_gate)
        {
            if (_disposed) return;
            _nextRemoteTry = 0;
            toLocal = chat.Remote is null && _onRemote;
        }
        _told = null;
        if (chat.Remote is not null) _ = TryRemoteAsync();
        else if (toLocal) _ = BackToLocalAsync();
    }

    async Task BackToLocalAsync()
    {
        await UseLocalAsync();
        await Tell("Connected to the local chat server.");
    }

    public void Send(ChatFrame frame)
    {
        ChatLink? link;
        lock (_gate) link = _link;
        link?.Send(frame);
    }

    async Task UseLocalAsync()
    {
        try { Attach(await Connect("127.0.0.1", chat.LocalPort), remote: false); }
        catch (IOException e) { chat.Log($"{name}: the local chat server refused: {e.Message}"); }
    }

    async Task TryRemoteAsync()
    {
        if (chat.Remote is not var (host, port) || Interlocked.Exchange(ref _trying, 1) != 0) return;
        try
        {
            var link = await Connect(host, port);
            // The mod changed its mind while this connected.
            if (chat.Remote != (host, port))
            {
                link.Dispose();
                return;
            }
            Attach(link, remote: true);
            chat.Log($"{name}: on chat server {host}:{port}");
            _told = null;
            await Tell("Connected to the online chat server." + (link.Name == name ? "" : $" Another player is named {name} there: you are {link.Name}."));
        }
        catch (IOException e)
        {
            chat.Log($"{name}: chat server {host}:{port}: {e.Message}");
            await FellBack($"Failed to connect to {host}:{port} chat channel, switching back to local chat server.");
        }
        finally { Volatile.Write(ref _trying, 0); }
    }

    async Task FellBack(string text)
    {
        lock (_gate) _nextRemoteTry = Environment.TickCount64 + RetryMilliseconds;
        if (_told == text) return;
        _told = text;
        chat.Log($"{name}: {text}");
        await Tell(text);
    }

    Task<ChatLink> Connect(string host, int port) => ChatLink.ConnectAsync(host, port, name, Received, Closed, _stop.Token);

    void Attach(ChatLink link, bool remote)
    {
        ChatLink? old;
        lock (_gate)
        {
            if (_disposed)
            {
                link.Dispose();
                return;
            }
            old = _link;
            _link = link;
            _onRemote = remote;
        }
        old?.Dispose();
        foreach (var (kind, channel, language) in chat.Channels.Current(name)) link.Send(new ChatFrame(ChatFrame.Join, Kind: kind, Channel: channel, Language: language));
    }

    void Closed(ChatLink link)
    {
        bool lostRemote;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(link, _link)) return;
            _link = null;
            lostRemote = _onRemote;
            _onRemote = false;
        }
        if (!lostRemote)
        {
            chat.Log($"{name}: lost the local chat server");
            return;
        }
        _ = UseLocalAsync();
        if (chat.Remote is var (host, port)) _ = FellBack($"Failed to connect to {host}:{port} chat channel, switching back to local chat server.");
    }

    /// <summary>A frame from the chat server: untrusted like all of it, so a name that could pose as a prefix is dropped.</summary>
    Task Received(ChatFrame frame)
    {
        string text = ChatWire.Clean(frame.Text);
        bool named = ChatWire.ValidMember(frame.Name);
        return frame.Type switch
        {
            ChatFrame.Line when named && frame.Kind is ChatChannelKind.Global or ChatChannelKind.Trade && text.Length > 0 =>
                Deliver(InstanceChat.ChatLine(text, InstanceChat.Sender(frame.Kind == ChatChannelKind.Global ? TypedChannel.Global : TypedChannel.Trade, frame.Name!)),
                    $"0x0017 {frame.Kind} chat line from {frame.Name}"),
            ChatFrame.Whisper when named && text.Length > 0 =>
                Deliver(InstanceChat.ChatLine(text, "@" + frame.Name), $"0x0017 whisper from {frame.Name}"),
            ChatFrame.Whispered when named && text.Length > 0 =>
                Deliver(InstanceChat.ChatLine(text, "@" + frame.Name, InstanceChat.WhisperSentFlag), $"0x0017 whisper to {frame.Name}"),
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
        ChatLink? link;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            link = _link;
            _link = null;
        }
        _stop.Cancel();
        link?.Dispose();
    }
}

using GameServer.Core.Protocol.Instance;

/// <summary>The chat server a mod asked for (<see cref="Use(string, string, int)"/>), for the Chat module to follow. It stays here so
/// that a mod and the module can each load and reload without the other. The last one asked wins; none means local chat.</summary>
public static class ChatRoutes
{
    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    public sealed class RemoteRoute
    {
        readonly Dictionary<(ChatChannelKind Kind, ushort Channel), (string Host, int Port)?>? rooms;
        readonly (string Host, int Port)? fallbackServer;
        public (string Host, int Port)? WhisperServer { get; }

        internal RemoteRoute((string Host, int Port)? whisperServer,
            IReadOnlyDictionary<(ChatChannelKind Kind, ushort Channel), (string Host, int Port)?>? channels,
            (string Host, int Port)? fallback = null)
        {
            WhisperServer = whisperServer;
            fallbackServer = fallback;
            rooms = channels is null ? null : new(channels);
        }

        public (string Host, int Port)? ServerFor(ChatChannelKind kind, ushort channel) =>
            rooms is null ? WhisperServer : rooms.TryGetValue((kind, channel), out var server) ? server : fallbackServer;
    }

    static readonly List<RemoteRoute> Asked = [];
    static readonly List<Action> Followers = [];
    static Func<string, ChatChannelKind, (ushort Channel, byte Language)?>? membership;

    /// <summary>The active Chat module's resolved Global or Trade channel for a character.</summary>
    public static (ushort Channel, byte Language)? Membership(string character, ChatChannelKind kind) =>
        Volatile.Read(ref membership)?.Invoke(character, kind);

    /// <summary>Registers the active Chat module's channel lookup until it unloads.</summary>
    public static IDisposable PublishMembership(Func<string, ChatChannelKind, (ushort Channel, byte Language)?> lookup)
    {
        Volatile.Write(ref membership, lookup);
        return new Undo(() => Interlocked.CompareExchange(ref membership, null, lookup));
    }

    public static (string Host, int Port)? Current => Selection?.WhisperServer;

    public static RemoteRoute? Selection
    {
        get { lock (Asked) return Asked.Count == 0 ? null : Asked[^1]; }
    }

    /// <summary>Share all Global, Trade and whisper chat with a remote server.</summary>
    public static IDisposable Use(string owner, string host, int port) => GameEventOwners.Own(owner, Use(host, port));

    /// <summary>Share whispers and the selected Global and Trade channel numbers with a remote server.</summary>
    public static IDisposable Use(string owner, string host, int port,
        IEnumerable<(ChatChannelKind Kind, ushort Channel)> channels) =>
        Use(owner, (host, port), channels.Distinct().ToDictionary(room => room, _ => (host, port)));

    /// <summary>Share each selected room with its configured endpoint, and use a separate endpoint for whispers.</summary>
    public static IDisposable Use(string owner, (string Host, int Port) whisperServer,
        IReadOnlyDictionary<(ChatChannelKind Kind, ushort Channel), (string Host, int Port)> channels) =>
        Use(owner, whisperServer, channels.ToDictionary(pair => pair.Key, pair => ((string Host, int Port)?)pair.Value), null);

    /// <summary>Route unlisted rooms to the default endpoint. An explicit null endpoint keeps that room local.</summary>
    public static IDisposable Use(string owner, (string Host, int Port)? whisperServer,
        IReadOnlyDictionary<(ChatChannelKind Kind, ushort Channel), (string Host, int Port)?> channels,
        (string Host, int Port)? fallbackServer) =>
        GameEventOwners.Own(owner, Register(new RemoteRoute(whisperServer, channels, fallbackServer)));

    public static IDisposable Use(string host, int port) => Register(new RemoteRoute((host, port), null));

    static IDisposable Register(RemoteRoute route)
    {
        lock (Asked) Asked.Add(route);
        Changed();
        return new Undo(() =>
        {
            lock (Asked) Asked.Remove(route);
            Changed();
        });
    }

    /// <summary>Runs when a remote endpoint or channel selection is registered or removed.</summary>
    public static IDisposable Follow(Action changed)
    {
        lock (Followers) Followers.Add(changed);
        return new Undo(() => { lock (Followers) Followers.Remove(changed); });
    }

    static void Changed()
    {
        Action[] followers;
        lock (Followers) followers = [.. Followers];
        foreach (var follower in followers) follower();
    }
}

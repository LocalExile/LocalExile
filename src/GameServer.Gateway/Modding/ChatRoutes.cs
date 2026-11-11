/// <summary>The chat server a mod asked for (<see cref="Use(string, string, int)"/>), for the Chat module to follow. It stays here so
/// that a mod and the module can each load and reload without the other. The last one asked wins; none means local chat.</summary>
public static class ChatRoutes
{
    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    static readonly List<(string Host, int Port)> Asked = [];
    static readonly List<Action> Followers = [];

    public static (string Host, int Port)? Current
    {
        get { lock (Asked) return Asked.Count == 0 ? null : Asked[^1]; }
    }

    /// <summary>Global, trade and whisper chat go through this chat server until the handle is disposed or its owner unloads, then back to
    /// the Gateway's local one. The players in game move at once.</summary>
    public static IDisposable Use(string owner, string host, int port) => GameEventOwners.Own(owner, Use(host, port));

    public static IDisposable Use(string host, int port)
    {
        lock (Asked) Asked.Add((host, port));
        Changed();
        return new Undo(() =>
        {
            lock (Asked) Asked.Remove((host, port));
            Changed();
        });
    }

    /// <summary>Runs after each change of <see cref="Current"/>.</summary>
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

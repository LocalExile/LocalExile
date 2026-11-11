using GameServer.Modding;

/// <summary>A keyed connection worker lease. State and host lifetimes are identities, never module callbacks.
/// Activation, completion and retirement are serialized by the connection's Monsters lock.</summary>
public sealed class ConnectionWorker(string key)
{
    static long epoch;
    public static long NextEpoch() => Interlocked.Increment(ref epoch);
    public string Key => key;
    IModuleLifetime? owner, source;
    object? state;
    long generation;
    bool running;
    IDisposable? ownerHandle, sourceHandle;

    public bool Bind(ClientState conn, IModuleLifetime rules, IModuleLifetime? provider,
        object runtime, out long started)
    {
        started = generation;
        if (!rules.IsActive || provider is { IsActive: false }) return false;
        if (ReferenceEquals(owner, rules) && ReferenceEquals(source, provider) && ReferenceEquals(state, runtime))
            return true;
        Retire();
        owner = rules; source = provider; state = runtime; started = ++generation;
        // Both resources are Gateway objects. The lifetime owns cleanup, not a closure into the old rule assembly.
        ownerHandle = rules.Own(new Retirement(conn, this, started));
        if (provider is not null) sourceHandle = provider.Own(new Retirement(conn, this, started));
        return Current(rules, provider, runtime, started);
    }

    public bool TryStart(ClientState conn, IModuleLifetime rules, IModuleLifetime? provider,
        object runtime, out long started)
    {
        if (!Bind(conn, rules, provider, runtime, out started) || running) return false;
        running = true;
        return true;
    }

    public bool Current(IModuleLifetime rules, IModuleLifetime? provider, object runtime, long started) =>
        generation == started && rules.IsActive && (provider?.IsActive ?? true)
        && ReferenceEquals(owner, rules) && ReferenceEquals(source, provider) && ReferenceEquals(state, runtime);

    public void Retire()
    {
        owner = source = null; state = null; running = false; generation++;
        Interlocked.Exchange(ref ownerHandle, null)?.Dispose();
        Interlocked.Exchange(ref sourceHandle, null)?.Dispose();
    }

    public void Complete(IModuleLifetime rules, IModuleLifetime? provider, object runtime, long started)
    {
        if (generation == started && ReferenceEquals(owner, rules) && ReferenceEquals(source, provider)
            && ReferenceEquals(state, runtime)) running = false;
    }

    sealed class Retirement(ClientState conn, ConnectionWorker worker, long started) : IDisposable
    {
        public void Dispose()
        {
            lock (conn.Monsters)
            {
                if (worker.generation != started) return;
                if (ReferenceEquals(worker, conn.ResourceEffectsLease)) conn.RetireResourceEffects();
                else if (ReferenceEquals(worker, conn.PlayerUpdatesWorker)) conn.RetirePlayerUpdates();
                else
                {
                    conn.Rampage.Clear();
                    conn.RetireOwnedCombat();
                }
            }
        }
    }
}

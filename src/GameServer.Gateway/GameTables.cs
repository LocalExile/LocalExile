/// <summary>
/// Loads on a thread of their own (<see cref="Isolated{T}"/>), so that the buffers a big parse rents go with the thread.
/// </summary>
public static class GameTables
{
    /// <summary>
    /// Runs a load on a thread of its own and waits for it. JsonDocument rents its buffers from ArrayPool&lt;byte&gt;.Shared,
    /// which keeps a returned buffer in the returning thread's slot (and per-core stacks) with no idle trim: the startup
    /// loads (Mods.json alone rents a 128 MB buffer) left ~240 MB of them in the main thread for the life of the
    /// Gateway. A thread that ends takes its slots with it. The load's result and exceptions are the caller's. When no
    /// thread can be started (out of memory or threads), the load runs on the calling thread, as it did before.
    /// </summary>
    public static T Isolated<T>(Func<T> load)
    {
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        Thread thread;
        try
        {
            thread = new Thread(() =>
            {
                try { result = load(); }
                catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            }, 16 << 20) { Name = "game data load", IsBackground = true };
            thread.Start();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            return load();
        }
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <inheritdoc cref="Isolated{T}(Func{T})"/>
    public static void Isolated(Action load) => Isolated(() => { load(); return 0; });
}

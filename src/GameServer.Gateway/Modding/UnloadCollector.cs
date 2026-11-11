/// <summary>Frees unloaded mods' load contexts. A reload unloads and loads many modules in a row, and a forced collection stops
/// every player's tick, so one pass runs after the last change of the whole wave, for all of them. A context still alive after
/// it stays listed: an ordinary collection may free it later, and its private copy is deleted at the end of a later pass.</summary>
public static class UnloadCollector
{
    sealed record Unloaded(WeakReference Context, string CopyDirectory, Action<int> StillInMemory, Action CopyStuck, TaskCompletionSource<bool> Done);

    // A wave's contexts are freed in layers: a module's own goes only after those of the modules that depend on it, and
    // each layer takes two rounds (a finalizer frees its loader allocator in one, the next collects the context). Live,
    // with a player in game and nothing holding a module, a reload of the game module left 24, 9, 9, 2, 2 and 0 alive
    // after rounds 1 to 6, the game module last. How deep the layers go depends on what the modules use of each other, so
    // a pass goes on for as long as its rounds free something: it stops after IdleRounds in a row that freed nothing
    // (what is left then is held), and after MaxRounds at the latest.
    const int MaxRounds = 12, IdleRounds = 3;
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan BetweenRounds = TimeSpan.FromMilliseconds(500);

    static readonly Lock Gate = new();
    static readonly Timer Pass = new(_ => Start());
    static List<Unloaded> waiting = [];
    static readonly List<Unloaded> lingering = [];
    static bool running;

    /// <summary>Completes when the context was collected (true) or still was not after its pass (false).</summary>
    /// <param name="stillInMemory">Told the number of rounds the pass ran.</param>
    public static Task<bool> Add(WeakReference context, string copyDirectory, Action<int> stillInMemory, Action copyStuck)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            waiting.Add(new(context, copyDirectory, stillInMemory, copyStuck, done));
            Pass.Change(Settle, Timeout.InfiniteTimeSpan);
        }
        return done.Task;
    }

    /// <summary>A module loaded: the wave is still going, so the pass waits for its end.</summary>
    public static void Postpone()
    {
        lock (Gate) if (waiting.Count > 0) Pass.Change(Settle, Timeout.InfiniteTimeSpan);
    }

    static void Start()
    {
        List<Unloaded> batch;
        lock (Gate)
        {
            if (waiting.Count == 0) return;
            if (running) { Pass.Change(Settle, Timeout.InfiniteTimeSpan); return; }
            running = true;
            batch = waiting;
            waiting = [];
        }
        _ = Collect(batch);
    }

    static async Task Collect(List<Unloaded> batch)
    {
        try
        {
            int rounds = 0;
            for (int alive = batch.Count, idle = 0; rounds < MaxRounds && alive > 0 && idle < IdleRounds; rounds++)
            {
                if (rounds > 0) await Task.Delay(BetweenRounds);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                int left = batch.Count(u => u.Context.IsAlive);
                idle = left < alive ? 0 : idle + 1;
                alive = left;
            }
            foreach (var unloaded in batch)
            {
                bool collected = !unloaded.Context.IsAlive;
                if (!collected) { unloaded.StillInMemory(rounds); lingering.Add(unloaded); }
                else if (unloaded.CopyDirectory.Length > 0 && !ModLoadContext.TryDeleteCopy(unloaded.CopyDirectory)) unloaded.CopyStuck();
                unloaded.Done.TrySetResult(collected);
            }
            lingering.RemoveAll(u => !u.Context.IsAlive && ModLoadContext.TryDeleteCopy(u.CopyDirectory));
        }
        finally
        {
            lock (Gate) running = false;
        }
    }
}

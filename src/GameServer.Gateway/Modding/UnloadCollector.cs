/// <summary>Frees unloaded mods' load contexts. A reload unloads and loads many modules in a row, and a forced collection stops
/// every player's tick, so one pass runs after the last change of the whole wave, for all of them. A context still alive after
/// it stays listed: an ordinary collection may free it later, and its private copy is deleted at the end of a later pass.</summary>
static class UnloadCollector
{
    sealed record Unloaded(WeakReference Context, string CopyDirectory, Action StillInMemory, Action CopyStuck, TaskCompletionSource<bool> Done);

    const int Rounds = 5;
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan BetweenRounds = TimeSpan.FromMilliseconds(500);

    static readonly Lock Gate = new();
    static readonly Timer Pass = new(_ => Start());
    static List<Unloaded> waiting = [];
    static readonly List<Unloaded> lingering = [];
    static bool running;

    /// <summary>Completes when the context was collected (true) or still was not after its pass (false).</summary>
    public static Task<bool> Add(WeakReference context, string copyDirectory, Action stillInMemory, Action copyStuck)
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
            for (int i = 0; i < Rounds && batch.Any(u => u.Context.IsAlive); i++)
            {
                if (i > 0) await Task.Delay(BetweenRounds);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            foreach (var unloaded in batch)
            {
                bool collected = !unloaded.Context.IsAlive;
                if (!collected) { unloaded.StillInMemory(); lingering.Add(unloaded); }
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

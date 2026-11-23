/// <summary>
/// Work split across the cores inside one frame. <see cref="Run"/> computes each item's result in parallel, from what
/// the area was when the phase began (compute must only read the area, and write nothing but its result), then applies the
/// results one by one in the items' order, so the outcome is the same whatever the threads did. <see cref="ForEach"/> is
/// for work where each item changes only its own data.
/// </summary>
public static class AreaJobs
{
    /// <summary>Items per job: one job per item would cost more to schedule than it saves.</summary>
    public const int Chunk = 256;

    public static void Run<T, R>(IReadOnlyList<T> items, Func<T, R> compute, Action<T, R> apply)
    {
        var results = new R[items.Count];
        Parallel.For(0, (items.Count + Chunk - 1) / Chunk, c =>
        {
            for (int i = c * Chunk, end = Math.Min(items.Count, i + Chunk); i < end; i++) results[i] = compute(items[i]);
        });
        for (int i = 0; i < items.Count; i++) apply(items[i], results[i]);
    }

    public static void ForEach<T>(IReadOnlyList<T> items, Action<T> body) =>
        Parallel.For(0, (items.Count + Chunk - 1) / Chunk, c =>
        {
            for (int i = c * Chunk, end = Math.Min(items.Count, i + Chunk); i < end; i++) body(items[i]);
        });
}

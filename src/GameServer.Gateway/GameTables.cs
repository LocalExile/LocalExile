using System.Text.Json;

/// <summary>
/// Shared parses of the big game tables (data/tables/*.json) that several catalogues read: Stats.json (the stat and mod
/// catalogues), MonsterVarieties.json (the monster and monster-mod catalogues), GrantedEffects.json (the skill
/// catalogue's tables, for the standard and the Ruthless catalogue). They load in bursts (the first player, the first
/// area with monsters), so a table opened again within <see cref="Keep"/> of its last use is not parsed again. A lease
/// is a document's user: dispose it like the document (<c>using var d = GameTables.Open(path)</c>); the document is
/// disposed when nobody has used it for <see cref="Keep"/>, or by <see cref="Release"/>. A file written since is parsed
/// again (the key holds its size and write time).
/// </summary>
static class GameTables
{
    /// <summary>How long an unused parse is kept for the next catalogue of the burst.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromSeconds(2);

    internal sealed class Entry(JsonDocument document)
    {
        public readonly JsonDocument Document = document;
        public int Users;
        public Timer? Expiry;
    }

    static readonly Lock Gate = new();
    static readonly Dictionary<(string Path, long Length, DateTime Written), Entry> Entries = [];
    static long _parses, _reuses;

    /// <summary>How many tables were parsed, and how many opens reused a parse (the perf benchmarks read them).</summary>
    public static (long Parses, long Reuses) Counts => (Interlocked.Read(ref _parses), Interlocked.Read(ref _reuses));

    /// <summary>A use of the table's parse; dispose it when done with its elements.</summary>
    public sealed class Lease : IDisposable
    {
        readonly (string, long, DateTime) key;
        Entry? entry;

        internal Lease((string, long, DateTime) key, Entry entry) { this.key = key; this.entry = entry; }

        public JsonElement RootElement => (entry ?? throw new ObjectDisposedException(nameof(Lease))).Document.RootElement;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref entry, null) is not { } used) return;
            lock (Gate)
                if (--used.Users == 0)
                    used.Expiry = new Timer(_ => Expire(key, used), null, Keep, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The table at <paramref name="path"/>, parsed now or shared with another user of the same file.</summary>
    public static Lease Open(string path)
    {
        var file = new FileInfo(path);
        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        lock (Gate)
            if (Entries.TryGetValue(key, out var shared))
            {
                shared.Users++;
                shared.Expiry?.Dispose();
                shared.Expiry = null;
                Interlocked.Increment(ref _reuses);
                return new(key, shared);
            }
        // Parsed outside the lock, on a thread of its own (its pool buffers go with it); a concurrent parse of the same
        // file keeps the first one.
        byte[] bytes = File.ReadAllBytes(path);
        var document = Isolated(() => JsonDocument.Parse(bytes));
        Interlocked.Increment(ref _parses);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var raced))
            {
                document.Dispose();
                raced.Users++;
                raced.Expiry?.Dispose();
                raced.Expiry = null;
                return new(key, raced);
            }
            var entry = new Entry(document) { Users = 1 };
            Entries[key] = entry;
            return new(key, entry);
        }
    }

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

    /// <summary>Disposes every parse nobody is using now (the Gateway calls it once its startup loads are done).</summary>
    public static void Release()
    {
        var idle = new List<Entry>();
        lock (Gate)
            foreach (var (key, entry) in Entries.Where(e => e.Value.Users == 0).ToList())
            {
                entry.Expiry?.Dispose();
                Entries.Remove(key);
                idle.Add(entry);
            }
        foreach (var entry in idle) Dispose(entry);
    }

    static void Expire((string, long, DateTime) key, Entry entry)
    {
        lock (Gate)
        {
            if (entry.Users > 0 || !Entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            Entries.Remove(key);
            entry.Expiry?.Dispose();
        }
        // Out of the table, so nobody can lease it again: disposed outside the lock, on a timer thread that must not fail.
        try { Dispose(entry); }
        catch (Exception ex) { Console.WriteLine($"[gateway] game tables: a parse was not disposed ({ex.GetType().Name}: {ex.Message})"); }
    }

    // Its buffers go back to the pool of a thread that ends (Isolated; inline when no thread can start).
    static void Dispose(Entry entry) => Isolated(entry.Document.Dispose);
}

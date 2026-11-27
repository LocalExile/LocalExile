using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

public enum LatencyPhase
{
    InputQueue, Handler, SendWait, SendHold, WireWait, WireHold, SocketWrite,
    CombatWait, CombatHold, ChannelLate, Surround, BlockerBuild, Message, Count,
}

/// <summary>Monotonic server timings, independent of the client's tick/latency display. No account/character tags.</summary>
public sealed class ConnectionPerformance
{
    static readonly Meter Meter = new("LocalExile.Gateway", "1.0");
    static readonly Histogram<double> Milliseconds = Meter.CreateHistogram<double>("localexile.gateway.duration", "ms");
    static readonly Histogram<long> Allocations = Meter.CreateHistogram<long>("localexile.gateway.surround.allocations", "By");
    static readonly UpDownCounter<long> Pending = Meter.CreateUpDownCounter<long>("localexile.gateway.pending_batches");
    static readonly string[] Names = Enum.GetNames<LatencyPhase>();
    static readonly string?[] MessageIds = new string?[ushort.MaxValue + 1];   // "0x0025", made once per id
    static readonly bool Verbose = GameServer.Core.Diagnostics.ServerTrace.Latency;
    /// <summary>The lag probe (PR #624's measurements: [latency-totals], [latency-sends], [latency-empty], [slow-use],
    /// [slow-send], the stat-build count): off unless Game_TRACE names <c>lag</c>, since it costs work of its own (a stack
    /// trace per empty send, a thread-time read per message).</summary>
    public static readonly bool LagProbe =
        (Environment.GetEnvironmentVariable("Game_TRACE") ?? "").Contains("lag", StringComparison.OrdinalIgnoreCase);   // Game_TRACE=latency (or Game_LATENCY_METRICS=1)
    readonly Sample[] samples = Enumerable.Range(0, (int)LatencyPhase.Count).Select(_ => new Sample()).ToArray();
    long reportedAt = Stopwatch.GetTimestamp();
    int pendingBatches;
    public void QueueDelta(int value) { Interlocked.Add(ref pendingBatches, value); Pending.Add(value); }

    sealed class Sample
    {
        public long Count, Total, Maximum, IntervalMaximum;
    }

    public readonly record struct Snapshot(long Count, double TotalMilliseconds, double MaximumMilliseconds);

    public void Elapsed(LatencyPhase phase, long start, string operation = "all") =>
        RecordTicks(phase, Math.Max(0, Stopwatch.GetTimestamp() - start), operation);

    public void RecordMilliseconds(LatencyPhase phase, double ms, string operation = "all") =>
        RecordTicks(phase, (long)(Math.Max(0, ms) * Stopwatch.Frequency / 1000), operation);

    /// <summary>Times one client message's handling (<c>using var</c> in the dispatch loop): the Message phase, its id as
    /// the operation, so a tool reading the meter sees what each message costs.</summary>
    public MessageScope Message(ushort id) => new(this, MessageIds[id] ??= $"0x{id:x4}");

    public readonly struct MessageScope(ConnectionPerformance owner, string operation) : IDisposable
    {
        readonly long startedAt = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            owner.Elapsed(LatencyPhase.Message, startedAt, operation);
            if (LagProbe) owner.Total(owner.handlers, operation, Stopwatch.GetTimestamp() - startedAt);
        }
    }

    /// <summary>A server-wide duration outside any connection (an area's level generation): the meter only, as
    /// <paramref name="phase"/> with <paramref name="operation"/>; the connections' log summaries do not show it.</summary>
    public static void Server(string phase, TimeSpan took, string operation) =>
        Milliseconds.Record(took.TotalMilliseconds, new("phase", phase), new("operation", operation));

    void RecordTicks(LatencyPhase phase, long ticks, string operation)
    {
        var sample = samples[(int)phase];
        Interlocked.Increment(ref sample.Count);
        Interlocked.Add(ref sample.Total, ticks);
        Maximum(ref sample.Maximum, ticks);
        Maximum(ref sample.IntervalMaximum, ticks);
        if (LagProbe && phase is LatencyPhase.InputQueue or LatencyPhase.Message)
            (phase is LatencyPhase.InputQueue ? inputQueue : messages).Add(ticks);
        Milliseconds.Record(ticks * 1000.0 / Stopwatch.Frequency,
            new("phase", Names[(int)phase]), new("operation", operation));
    }

    static void Maximum(ref long field, long value)
    {
        long before = Volatile.Read(ref field);
        while (value > before)
        {
            long found = Interlocked.CompareExchange(ref field, value, before);
            if (found == before) break;
            before = found;
        }
    }

    public Snapshot Read(LatencyPhase phase)
    {
        var sample = samples[(int)phase];
        return new(Volatile.Read(ref sample.Count), Volatile.Read(ref sample.Total) * 1000.0 / Stopwatch.Frequency,
            Volatile.Read(ref sample.Maximum) * 1000.0 / Stopwatch.Frequency);
    }

    public void SurroundAllocation(long bytes) => Allocations.Record(Math.Max(0, bytes));

    // Wrap only the hot simulation critical sections. Monitor retains lock's re-entrancy and exception semantics.
    public LockScope Lock(object gate, string operation) => new(this, gate, operation);

    public readonly struct LockScope : IDisposable
    {
        readonly ConnectionPerformance owner;
        readonly object gate;
        readonly string operation;
        readonly long enteredAt;
        public LockScope(ConnectionPerformance owner, object gate, string operation)
        {
            this.owner = owner; this.gate = gate; this.operation = operation;
            long start = Stopwatch.GetTimestamp();
            Monitor.Enter(gate);
            enteredAt = Stopwatch.GetTimestamp();
            try
            {
                owner.RecordTicks(LatencyPhase.CombatWait, enteredAt - start, operation);
                // Which phase is holding the combat lock, and for how long: the aggregate CombatWait/CombatHold cannot say
                // (live 2026-11-17: 500-600 ms holds during an area entry, and every phase looked alike).
                if (LagProbe && Stopwatch.GetElapsedTime(start, enteredAt).TotalMilliseconds >= 50)
                    Console.WriteLine($"[slow-lock] {operation} wait={Stopwatch.GetElapsedTime(start, enteredAt).TotalMilliseconds:0} ms");
            }
            catch { Monitor.Exit(gate); throw; }
        }
        public void Dispose()
        {
            long ticks = Stopwatch.GetTimestamp() - enteredAt;
            Monitor.Exit(gate);
            owner.RecordTicks(LatencyPhase.CombatHold, ticks, operation);
            if (LagProbe) owner.Total(owner.locks, operation, ticks);
            if (LagProbe && ticks * 1000.0 / Stopwatch.Frequency >= 50)
                Console.WriteLine($"[slow-lock] {operation} hold={ticks * 1000.0 / Stopwatch.Frequency:0} ms");
        }
    }

    /// <summary>At most one summary per ten seconds; default output requires a >=50 ms interval sample.</summary>
    public void MaybeLog(string remote, int pending = -1)
    {
        long now = Stopwatch.GetTimestamp(), before = Volatile.Read(ref reportedAt);
        if (Stopwatch.GetElapsedTime(before, now).TotalSeconds < 10
            || Interlocked.CompareExchange(ref reportedAt, now, before) != before) return;
        Span<long> peaks = stackalloc long[(int)LatencyPhase.Count];
        bool slow = false;
        for (int i = 0; i < peaks.Length; i++)
        {
            peaks[i] = Interlocked.Exchange(ref samples[i].IntervalMaximum, 0);
            slow |= peaks[i] * 1000.0 / Stopwatch.Frequency >= 50;
        }
        if (!Verbose && !slow) return;
        var line = new StringBuilder($"[latency] {remote} pending-batches={(pending < 0 ? Volatile.Read(ref pendingBatches) : pending)} interval-max-ms");
        for (int i = 0; i < peaks.Length; i++)
            line.Append($" {Names[i]}={peaks[i] * 1000.0 / Stopwatch.Frequency:0.00}");
        Console.WriteLine(line);
        // Temporary lag instrumentation (remove with the investigation): totals against the interval, so the maxima
        // above can be read as work per second, and the runtime's own costs beside them.
        if (!LagProbe) return;
        var totals = new StringBuilder($"[latency-totals] {remote} interval={Stopwatch.GetElapsedTime(before, now).TotalSeconds:0.0}s");
        for (int i = 0; i < peaks.Length; i++)
            totals.Append($" {Names[i]}={Read((LatencyPhase)i).TotalMilliseconds:0}x{Read((LatencyPhase)i).Count}");
        totals.Append(' ').Append(RuntimeSummary(before));
        long builds = Interlocked.Exchange(ref statBuilds, 0), buildTicks = Interlocked.Exchange(ref statBuildTicks, 0);
        double interval = Stopwatch.GetElapsedTime(before, now).TotalSeconds;
        totals.Append(FormattableString.Invariant($" stat-builds={builds / Math.Max(.001, interval):0.00}/s={buildTicks * 100.0 / Stopwatch.Frequency / Math.Max(.001, interval):0.00}%cpu"));
        totals.Append($" bytes-out={Interlocked.Exchange(ref bytesOut, 0) / 1024.0 / Math.Max(.001, interval):0}KB/s");
        // The first interval of a connection carries one-time work (the first stat build, the first use of a skill).
        if (Interlocked.Exchange(ref reported, 1) == 0) totals.Append(" first-interval");
        Console.WriteLine(totals);
        LogLookups(remote, interval);
        LogTotals("[latency-locks]", remote + " (measured phases only)", locks);
        LogTotals("[latency-handlers]", remote, handlers);
        Console.WriteLine($"[latency-typical] {remote} InputQueue {inputQueue.Take()} Message {messages.Take()}");
        LogCounts(remote);
        var kinds = new StringBuilder($"[latency-sends] {remote}");
        var taken = TakeSends();
        for (int i = 0; i < Math.Min(10, taken.Length); i++)
            kinds.Append($" 0x{taken[i].Id:x4}={taken[i].Ticks * 1000.0 / Stopwatch.Frequency:0}msx{taken[i].Count}/{taken[i].Bytes / 1024.0:0}KB");
        Console.WriteLine(kinds);
        var empty = new StringBuilder($"[latency-empty] {remote} gate={Read(LatencyPhase.SendHold).TotalMilliseconds:0}ms");
        var emptyTaken = TakeEmptySends();
        for (int i = 0; i < Math.Min(10, emptyTaken.Length); i++)
            empty.Append($" {emptyTaken[i].Caller}={emptyTaken[i].Ticks * 1000.0 / Stopwatch.Frequency:0}msx{emptyTaken[i].Count}");
        Console.WriteLine(empty);
    }

    // ---- The lag probe's interval counters (Game_TRACE=lag; Agents/docs/dev/debugging.md). Each costs one check of
    // LagProbe when the probe is off and never allocates then: the callers sit in the hottest paths of the server.

    readonly Dictionary<string, (long Count, long Ticks)> locks = [], handlers = [];
    readonly Dictionary<string, long> counts = [];
    readonly Reservoir inputQueue = new(), messages = new();
    long bytesOut;
    int reported;

    void Total(Dictionary<string, (long Count, long Ticks)> into, string name, long ticks)
    {
        lock (into)
        {
            into.TryGetValue(name, out var sample);
            into[name] = (sample.Count + 1, sample.Ticks + ticks);
        }
    }

    static void LogTotals(string tag, string who, Dictionary<string, (long Count, long Ticks)> from)
    {
        (string Name, long Count, long Ticks)[] taken;
        lock (from)
        {
            taken = [.. from.Select(p => (p.Key, p.Value.Count, p.Value.Ticks))];
            from.Clear();
        }
        Array.Sort(taken, (a, b) => b.Ticks.CompareTo(a.Ticks));
        var line = new StringBuilder($"{tag} {who}");
        for (int i = 0; i < Math.Min(12, taken.Length); i++)
            line.Append($" {taken[i].Name}={taken[i].Ticks * 1000.0 / Stopwatch.Frequency:0}msx{taken[i].Count}");
        Console.WriteLine(line);
    }

    /// <summary>One more of something worth a total: a refused skill use, a dropped projectile record, a stat packet
    /// sent for a cause. <paramref name="name"/> is a constant, so the call allocates nothing.</summary>
    public void Count(string name, long by = 1)
    {
        if (!LagProbe) return;
        lock (counts) counts[name] = counts.GetValueOrDefault(name) + by;
    }

    void LogCounts(string remote)
    {
        KeyValuePair<string, long>[] taken;
        lock (counts)
        {
            taken = [.. counts];
            counts.Clear();
        }
        if (taken.Length == 0) return;
        Array.Sort(taken, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        Console.WriteLine($"[latency-counts] {remote} " + string.Join(", ", taken.Select(p => $"{p.Key} x{p.Value}")));
    }

    long lookupHits, lookupHitTicks, lookupMisses, lookupMissTicks;
    readonly Dictionary<string, long> lookupCauses = [];
    static int coldLookup;

    /// <summary>The start of one timed step of the probe, or 0 when the probe is off (then the matching end does nothing).</summary>
    public static long ProbeStart() => LagProbe ? Stopwatch.GetTimestamp() : 0;

    /// <summary>A cached lookup (the item-stat sources) that found its inputs unchanged.</summary>
    public void LookupHit(long start)
    {
        if (start == 0) return;
        Interlocked.Increment(ref lookupHits);
        Interlocked.Add(ref lookupHitTicks, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>A cached lookup that had to build its result again, and the input that had changed.</summary>
    public void LookupMiss(long start, string cause)
    {
        if (start == 0) return;
        long ticks = Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref lookupMisses);
        Interlocked.Add(ref lookupMissTicks, ticks);
        lock (lookupCauses) lookupCauses[cause] = lookupCauses.GetValueOrDefault(cause) + 1;
        if (Interlocked.Exchange(ref coldLookup, 1) == 0)
            Console.WriteLine($"[cold] the first item-stat build of this process took {ticks * 1000.0 / Stopwatch.Frequency:0} ms (one-time loads and compilation, not a steady cost)");
    }

    void LogLookups(string remote, double interval)
    {
        long hits = Interlocked.Exchange(ref lookupHits, 0), hitTicks = Interlocked.Exchange(ref lookupHitTicks, 0);
        long misses = Interlocked.Exchange(ref lookupMisses, 0), missTicks = Interlocked.Exchange(ref lookupMissTicks, 0);
        KeyValuePair<string, long>[] causes;
        lock (lookupCauses)
        {
            causes = [.. lookupCauses];
            lookupCauses.Clear();
        }
        Array.Sort(causes, (a, b) => b.Value.CompareTo(a.Value));
        double us = 1e6 / Stopwatch.Frequency;
        Console.WriteLine($"[latency-lookup] {remote} item-stat lookups={(hits + misses) / Math.Max(.001, interval):0}/s "
            + $"hits={hits} ({(hits == 0 ? 0 : hitTicks * us / hits):0} us each) misses={misses} ({(misses == 0 ? 0 : missTicks * us / misses):0} us each)"
            + (causes.Length == 0 ? "" : " miss causes: " + string.Join(", ", causes.Take(6).Select(p => $"{p.Key} x{p.Value}"))));
    }

    readonly long createdAt = Stopwatch.GetTimestamp();

    /// <summary>The client reported its area generated and the server's entry work is done: how long the client took to
    /// get there from the connection's start (its own loading), and what the server's part cost.</summary>
    public void AreaLoaded(string remote, string area, long handlerStart)
    {
        if (!LagProbe && !Verbose) return;
        Console.WriteLine($"[load] {remote} area {area}: {Stopwatch.GetElapsedTime(createdAt, handlerStart).TotalSeconds:0.00} s from connect to the client's \"area generated\", "
            + $"then {Stopwatch.GetElapsedTime(handlerStart).TotalMilliseconds:0} ms of server entry work");
    }

    /// <summary>The interval's samples of one phase, for its typical value beside the maximum the summary prints.</summary>
    sealed class Reservoir
    {
        readonly long[] ticks = new long[1024];
        int count;
        public void Add(long value)
        {
            int at = Interlocked.Increment(ref count) - 1;
            if (at < ticks.Length) ticks[at] = value;
        }
        public string Take()
        {
            int n = Math.Min(Interlocked.Exchange(ref count, 0), ticks.Length);
            if (n == 0) return "n=0";
            long[] sorted = ticks[..n];
            Array.Sort(sorted);
            double ms = 1000.0 / Stopwatch.Frequency;
            return $"p50={sorted[n / 2] * ms:0.00} p95={sorted[Math.Min(n - 1, n * 95 / 100)] * ms:0.00} ms (n={n})";
        }
    }

    readonly Dictionary<string, long> syncs = [];
    long statBuilds, statBuildTicks;

    /// <summary>One full player-stat build and how long it took. Temporary lag instrumentation.</summary>
    public void StatBuild(long start) { if (LagProbe) StatBuildTicks(Stopwatch.GetTimestamp() - start); }

    void StatBuildTicks(long ticks)
    {
        Interlocked.Increment(ref statBuilds);
        Interlocked.Add(ref statBuildTicks, ticks);
    }

    /// <summary>At most one state sync of a kind per window: the update loops call their publishers every 16-32 ms,
    /// and each publish takes the send gate and <c>lock (conn.Monsters)</c> even when it has nothing new (2026-11-05:
    /// ~230 such sends a second, the gate held 58% of the time, and the input handlers queued 4-8 s behind them). The
    /// packets are idempotent state snapshots, so a skipped step's change goes out with the next publish.</summary>
    public bool SyncDue(string name, double milliseconds)
    {
        long now = Stopwatch.GetTimestamp();
        lock (syncs)
        {
            if (syncs.TryGetValue(name, out long last) && Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < milliseconds)
                return false;
            syncs[name] = now;
            return true;
        }
    }

    readonly Dictionary<ushort, (long Count, long Ticks, long Bytes)> sends = [];
    readonly Dictionary<string, (long Count, long Ticks)> emptySends = [];

    /// <summary>A sync send whose builder published nothing (it took the gate and the area's frame to find that out).
    /// Temporary lag instrumentation.</summary>
    public void EmptySend(string caller, long ticks)
    {
        if (!LagProbe) return;
        lock (sends)
        {
            emptySends.TryGetValue(caller, out var sample);
            emptySends[caller] = (sample.Count + 1, sample.Ticks + ticks);
        }
    }

    (string Caller, long Count, long Ticks)[] TakeEmptySends()
    {
        lock (sends)
        {
            var taken = new (string Caller, long Count, long Ticks)[emptySends.Count];
            int i = 0;
            foreach (var (caller, sample) in emptySends) taken[i++] = (caller, sample.Count, sample.Ticks);
            emptySends.Clear();
            Array.Sort(taken, (a, b) => b.Count.CompareTo(a.Count));
            return taken;
        }
    }

    /// <summary>One packet's gate hold, by message id. Temporary lag instrumentation.</summary>
    public void Send(ushort id, long ticks, int bytes = 0)
    {
        if (!LagProbe) return;
        Interlocked.Add(ref bytesOut, bytes);
        lock (sends)
        {
            sends.TryGetValue(id, out var sample);
            sends[id] = (sample.Count + 1, sample.Ticks + ticks, sample.Bytes + bytes);
        }
    }

    (ushort Id, long Count, long Ticks, long Bytes)[] TakeSends()
    {
        lock (sends)
        {
            var taken = new (ushort Id, long Count, long Ticks, long Bytes)[sends.Count];
            int i = 0;
            foreach (var (id, sample) in sends) taken[i++] = (id, sample.Count, sample.Ticks, sample.Bytes);
            sends.Clear();
            Array.Sort(taken, (a, b) => b.Ticks.CompareTo(a.Ticks));
            return taken;
        }
    }

    /// <summary>Interval GC pause time and allocation, with the thread pool's backlog: a delayed continuation and a
    /// collection both show up as a slow phase, and neither is the phase's own work. Temporary lag instrumentation.</summary>
    public static string RuntimeSummary(long since)
    {
        var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes();
        double seconds = Stopwatch.GetElapsedTime(since).TotalSeconds;
        double pauseDelta = pause - Interlocked.Exchange(ref lastPauseMs, (long)pause);
        long allocatedDelta = allocated - Interlocked.Exchange(ref lastAllocatedBytes, allocated);
        double cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        double cpuDelta = cpu - Interlocked.Exchange(ref lastCpuMs, (long)cpu);
        long reads = Saves.CharacterReads, readsDelta = reads - Interlocked.Exchange(ref lastSaveReads, reads);
        return $"gc-pause={pauseDelta:0}ms gc-alloc={allocatedDelta / (1024.0 * 1024.0) / Math.Max(.001, seconds):0}MB/s "
            + $"gc-gen2={GC.CollectionCount(2)} proc-cpu={cpuDelta / Math.Max(.001, seconds) / 10:0}% "
            + $"tp-threads={ThreadPool.ThreadCount} tp-pending={ThreadPool.PendingWorkItemCount} "
            + $"save-reads={readsDelta / Math.Max(.001, seconds):0}/s "
            + $"lock-contentions={(contentions = Monitor.LockContentionCount) - Interlocked.Exchange(ref lastContentions, contentions)}";
    }

    static long contentions, lastContentions = Monitor.LockContentionCount;

    static long lastPauseMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
    static long lastAllocatedBytes = GC.GetTotalAllocatedBytes();
    static long lastCpuMs = (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
    static long lastSaveReads;
}

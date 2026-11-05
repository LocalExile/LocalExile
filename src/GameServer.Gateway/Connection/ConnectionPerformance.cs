using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

enum LatencyPhase
{
    InputQueue, Handler, SendWait, SendHold, WireWait, WireHold, SocketWrite,
    CombatWait, CombatHold, ChannelLate, Surround, BlockerBuild, Message, Count,
}

/// <summary>Monotonic server timings, independent of the client's tick/latency display. No account/character tags.</summary>
sealed class ConnectionPerformance
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

    internal readonly record struct Snapshot(long Count, double TotalMilliseconds, double MaximumMilliseconds);

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
        public void Dispose() => owner.Elapsed(LatencyPhase.Message, startedAt, operation);
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
        internal LockScope(ConnectionPerformance owner, object gate, string operation)
        {
            this.owner = owner; this.gate = gate; this.operation = operation;
            long start = Stopwatch.GetTimestamp();
            Monitor.Enter(gate);
            enteredAt = Stopwatch.GetTimestamp();
            try { owner.RecordTicks(LatencyPhase.CombatWait, enteredAt - start, operation); }
            catch { Monitor.Exit(gate); throw; }
        }
        public void Dispose()
        {
            long ticks = Stopwatch.GetTimestamp() - enteredAt;
            Monitor.Exit(gate);
            owner.RecordTicks(LatencyPhase.CombatHold, ticks, operation);
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
        totals.Append($" stat-builds={builds / Math.Max(.001, interval):0}/s={buildTicks * 100.0 / Stopwatch.Frequency / Math.Max(.001, interval):0}%cpu");
        Console.WriteLine(totals);
        var kinds = new StringBuilder($"[latency-sends] {remote}");
        var taken = TakeSends();
        for (int i = 0; i < Math.Min(10, taken.Length); i++)
            kinds.Append($" 0x{taken[i].Id:x4}={taken[i].Ticks * 1000.0 / Stopwatch.Frequency:0}msx{taken[i].Count}");
        Console.WriteLine(kinds);
        var empty = new StringBuilder($"[latency-empty] {remote} gate={Read(LatencyPhase.SendHold).TotalMilliseconds:0}ms");
        var emptyTaken = TakeEmptySends();
        for (int i = 0; i < Math.Min(10, emptyTaken.Length); i++)
            empty.Append($" {emptyTaken[i].Caller}={emptyTaken[i].Ticks * 1000.0 / Stopwatch.Frequency:0}msx{emptyTaken[i].Count}");
        Console.WriteLine(empty);
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

    readonly Dictionary<ushort, (long Count, long Ticks)> sends = [];
    readonly Dictionary<string, (long Count, long Ticks)> emptySends = [];

    /// <summary>A sync send whose builder published nothing (it took the gate and the Monsters lock to find that out).
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
    public void Send(ushort id, long ticks)
    {
        if (!LagProbe) return;
        lock (sends)
        {
            sends.TryGetValue(id, out var sample);
            sends[id] = (sample.Count + 1, sample.Ticks + ticks);
        }
    }

    (ushort Id, long Count, long Ticks)[] TakeSends()
    {
        lock (sends)
        {
            var taken = new (ushort Id, long Count, long Ticks)[sends.Count];
            int i = 0;
            foreach (var (id, sample) in sends) taken[i++] = (id, sample.Count, sample.Ticks);
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
        return $"gc-pause={pauseDelta:0}ms gc-alloc={allocatedDelta / (1024.0 * 1024.0) / Math.Max(.001, seconds):0}MB/s "
            + $"gc-gen2={GC.CollectionCount(2)} proc-cpu={cpuDelta / Math.Max(.001, seconds) / 10:0}% "
            + $"tp-threads={ThreadPool.ThreadCount} tp-pending={ThreadPool.PendingWorkItemCount}";
    }

    static long lastPauseMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
    static long lastAllocatedBytes = GC.GetTotalAllocatedBytes();
    static long lastCpuMs = (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
}

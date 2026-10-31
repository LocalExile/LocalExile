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
    static readonly bool Verbose = GameServer.Core.Diagnostics.ServerTrace.Latency;   // Game_TRACE=latency (or Game_LATENCY_METRICS=1)
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
    }
}

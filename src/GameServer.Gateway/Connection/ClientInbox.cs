using System.Diagnostics;
using System.Threading.Channels;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// One connection's client messages, split as they are read (issue #46). The client's "network: N ms" is the time until
/// its oldest 0x1b tick is answered (Agents/docs/protocol/07 §1), so the read loop answers ticks at once, and every other
/// message waits here, in the order received, for the connection's one handler task. A slow handler (0x7d populating
/// the area, an area change) no longer holds the 0x1c answers back.
/// </summary>
sealed class ClientInbox(ConnectionPerformance? performance = null)
{
    /// <summary>A tick answered, or a handler run, later than this is logged.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromMilliseconds(50);

    /// <summary>The messages framed from one read, without its ticks, and the read's unframed rest.</summary>
    /// <param name="ReadAt">When the read completed (Stopwatch timestamp): a click's time on the client is this read time
    /// (the client applies it right after its frame tick, ~0.7 ms before the read; Agents/docs/protocol/07 §5).</param>
    public sealed record Batch(IReadOnlyList<(ushort Id, byte[] Bytes)> Messages, byte[] Unknown, long ReadAt = 0,
        long EnqueuedAt = 0);

    /// <summary>Reads in a row in which nothing frames after which the client's stream is taken as lost: its bytes no
    /// longer decrypt with our cipher (live 2026-10-30, after the Glyph Wall opened: every later message was garbage and
    /// the connection stayed open, deaf). A healthy connection frames every read. The client's ticks alone make about
    /// five reads a second.</summary>
    public const int LostAfterUnframedReads = 25;

    /// <summary>The run of reads that framed nothing, after one more read: back to 0 when anything framed.</summary>
    public static int UnframedRun(int run, int framed, int unknownBytes) => framed > 0 ? 0 : unknownBytes > 0 ? run + 1 : run;

    /// <summary>0x1c's index that clears the client's whole send-time queue (≥ its size, 0x142155930).</summary>
    public const ushort ClearQueue = ushort.MaxValue;

    /// <summary>
    /// 0x1c's server delay: 0. The client's 0x1c handler (0x14214b8dc) clamps its shown latency (+0xc490, F1 NETWORK and
    /// the latency graph) into [sample − delay, rtt − delay], each floored at 0. Each tick is answered only once the input
    /// read before it has been handled (DeferTick), so with no delay subtracted the meter shows the whole wait: the
    /// network, the client's next update and the server's input queue in a busy fight. 0xFFFF here (issue #49) zeroed
    /// both bounds and the meter read 0 always.
    /// </summary>
    public const ushort ServerDelayMs = 0;

    long _posted, _handled;
    readonly Queue<(long Target, Func<Task> Answer)> _ticks = new();
    Task _answering = Task.CompletedTask;

    /// <summary>Answers a tick once every batch queued before it has been handled, in tick order: the client then
    /// measures how long the server took to take in what it sent, not only the network.</summary>
    public void DeferTick(Func<Task> answer)
    {
        lock (_ticks) _ticks.Enqueue((Interlocked.Read(ref _posted), answer));
        AnswerDue();
    }

    /// <summary>The handler finished one batch: the ticks waiting for it go out.</summary>
    public void Handled()
    {
        Interlocked.Increment(ref _handled);
        AnswerDue();
    }

    void AnswerDue()
    {
        lock (_ticks)
        {
            long handled = Interlocked.Read(ref _handled);
            while (_ticks.TryPeek(out var tick) && tick.Target <= handled)
            {
                _ticks.Dequeue();
                var answer = tick.Answer;
                _answering = _answering.ContinueWith(async _ => { try { await answer(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } },
                    TaskScheduler.Default).Unwrap();
            }
        }
    }

    readonly Channel<Batch> _queue = Channel.CreateUnbounded<Batch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    bool _resync = true;
    int pending;
    public int PendingBatches => Volatile.Read(ref pending);

    /// <summary>
    /// The ack index for the next 0x1c. The client keeps the send time of every 0x1b in a queue and index i drops entries
    /// 0..i. Ticks no server answered (sent while the client left the last area, or lost in unframed bytes) stay there
    /// for good, and index 0 then always pops a stale entry: the meter trailed by two tick intervals (~330 ms, live
    /// 2026-10-19). So the first 0x1c of a connection, and the first after an unframed read, clears the queue; the
    /// client then measures from its latest tick (+0xc470). The rest answer one tick each.
    /// </summary>
    public ushort NextAckIndex()
    {
        if (!_resync) return 0;
        _resync = false;
        return ClearQueue;
    }

    /// <summary>Queues one read's messages for the handler task and returns its ticks, for the read loop to answer.</summary>
    public List<byte[]> Post(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, long readAt = 0)
    {
        if (unknown.Length > 0) _resync = true;   // a tick may be in the bytes that could not be framed
        var ticks = new List<byte[]>();
        var rest = new List<(ushort, byte[])>();
        foreach (var m in messages)
            if (m.Id == InstanceProtocol.TickId) ticks.Add(m.Bytes);
            else rest.Add(m);
        if (rest.Count > 0 || unknown.Length > 0)
        {
            Interlocked.Increment(ref pending);
            performance?.QueueDelta(1);
            if (!_queue.Writer.TryWrite(new Batch(rest, unknown, readAt, System.Diagnostics.Stopwatch.GetTimestamp())))
            {
                Interlocked.Decrement(ref pending);
                performance?.QueueDelta(-1);
            }
            else Interlocked.Increment(ref _posted);
        }
        return ticks;
    }

    /// <summary>No more reads: the handler task ends after the queued batches.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    // Called after completing the writer and stopping enumeration, when a closed connection abandons its backlog.
    public void DiscardPending()
    {
        while (_queue.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref pending);
            performance?.QueueDelta(-1);
        }
        lock (_ticks) _ticks.Clear();
    }

    public async IAsyncEnumerable<Batch> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var batch in _queue.Reader.ReadAllAsync(token))
        {
            Interlocked.Decrement(ref pending);
            performance?.QueueDelta(-1);
            performance?.Elapsed(LatencyPhase.InputQueue, batch.ReadAt != 0 ? batch.ReadAt : batch.EnqueuedAt);
            yield return batch;
        }
    }
}

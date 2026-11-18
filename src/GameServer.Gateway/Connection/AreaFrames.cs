using System.Diagnostics;
using GameServer.Core.Diagnostics;

/// <summary>
/// The frame clock of a connection's world, driven by its frame writer (Program.RunWorldFrames). At each frame boundary
/// the writer waits, at most one frame, for the previous step's loops to settle (each has waited again), ends the frame,
/// and then steps: every loop wait that falls due completes on the writer's own thread, in PlayerUpdateRole order, so the
/// loops run one after another at the boundary instead of on timers of their own. What a step sends then goes out in the
/// frame that starts at the boundary, the one its frame time (Time) names.
/// </summary>
/// <remarks>
/// A wait is a whole number of frames, rounded to the nearest (at least 1): 16 and 32 ms are 1 frame, 50 ms 2, 100 ms 3,
/// 250 ms 8. The loops integrate the time that passed between their steps, so their per-second rates are kept. While no
/// writer runs (before the first frame, after the connection's frames end), a wait is a plain delay.
/// </remarks>
public sealed class AreaFrames
{
    public const int BudgetFrames = 256;
    static readonly int Roles = Enum.GetValues<PlayerUpdateRole>().Length;

    sealed class Waiter(long due, PlayerUpdateRole role, long order) : TaskCompletionSource(TaskCreationOptions.None)
    {
        public readonly long Due = due, Order = order;
        public readonly PlayerUpdateRole Role = role;
        public CancellationTokenRegistration Cancel;
    }

    /// <summary>One frame's costs: the step's loops run on the writer (StepMs), the wait for the ones that went on
    /// asynchronously (SettleMs), and the frame's write (WriteMs, Records, Bytes). RoleMs[r] is how long after the step's
    /// loops of role r ran on the writer in the step (up to their next await); Contention is the process's lock contentions during it.</summary>
    public readonly record struct FrameBudget(long Frame, double Time, double StepMs, double SettleMs, double WriteMs,
        int Records, int Bytes, long Contention, int Unsettled, double[] RoleMs)
    {
        public double TotalMs => StepMs + SettleMs + WriteMs;
    }

    readonly Lock gate = new();
    readonly List<Waiter> waiting = [];
    readonly int[] active = new int[Roles];
    readonly double[] roleMs = new double[Roles];
    readonly FrameBudget[] ring = new FrameBudget[BudgetFrames];
    int activeTotal, unsettled;
    long order, ringCount, stepStarted, contentionAtStep;
    double stepMs, settleMs;
    string unsettledRoles = "";
    TaskCompletionSource? settled;

    /// <summary>Frames stepped so far.</summary>
    public long Frame { get; private set; }
    /// <summary>The time on the connection's Clock of the last step's boundary, NaN before the first.</summary>
    public double Time { get; private set; } = double.NaN;
    public bool Running { get; private set; }

    /// <summary>The frame time for the loops: the last boundary stepped while the frames run, else the Clock.</summary>
    public double Now(ClientState conn) => Running && !double.IsNaN(Time) ? Time : conn.Clock.Elapsed.TotalSeconds;

    public static int FramesFor(int milliseconds) =>
        Math.Max(1, (int)Math.Round(milliseconds / (double)Math.Max(1, (int)Program.FrameMs), MidpointRounding.AwayFromZero));

    public Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping)
    {
        if (stopping.IsCancellationRequested) return Task.FromCanceled(stopping);
        Waiter waiter;
        lock (gate)
        {
            if (!Running) return Task.Delay(Math.Max(0, milliseconds), stopping);
            Settled(role);
            waiter = new(Frame + FramesFor(milliseconds), role, order++);
            waiting.Add(waiter);
        }
        if (stopping.CanBeCanceled)
            waiter.Cancel = stopping.Register(static o =>
            {
                var (frames, w) = ((AreaFrames, Waiter))o!;
                lock (frames.gate) frames.waiting.Remove(w);
                w.TrySetCanceled();
            }, (this, waiter));
        return waiter.Task;
    }

    // A role's loop that waits again has finished its step.
    void Settled(PlayerUpdateRole role)
    {
        int r = (int)role;
        if (active[r] == 0) return;
        active[r]--;
        if (--activeTotal == 0) { settled?.TrySetResult(); settled = null; }
    }

    /// <summary>A role's loop ended: it will not wait again this step.</summary>
    public void Ended(PlayerUpdateRole role)
    {
        lock (gate) Settled(role);
    }

    public void Start()
    {
        lock (gate) Running = true;
    }

    /// <summary>The frames end: every waiting loop resumes now, and its next waits are plain delays.</summary>
    public void Stop()
    {
        Waiter[] all;
        lock (gate)
        {
            Running = false;
            all = [.. waiting];
            waiting.Clear();
            Array.Clear(active);
            activeTotal = 0;
            settled?.TrySetResult(); settled = null;
        }
        foreach (var w in all) { w.Cancel.Dispose(); w.TrySetResult(); }
    }

    /// <summary>Steps the frame at <paramref name="time"/>: the due waits complete here, in role order.</summary>
    public void Step(double time)
    {
        List<Waiter> due = [];
        lock (gate)
        {
            if (!Running) return;
            Frame++;
            Time = time;
            stepStarted = Stopwatch.GetTimestamp();
            contentionAtStep = Monitor.LockContentionCount;
            Array.Clear(roleMs);
            stepMs = settleMs = 0;
            unsettled = 0;
            for (int i = waiting.Count - 1; i >= 0; i--)
                if (waiting[i].Due <= Frame) { due.Add(waiting[i]); waiting.RemoveAt(i); }
            due.Sort(static (a, b) => a.Role != b.Role ? a.Role.CompareTo(b.Role) : a.Order.CompareTo(b.Order));
            // A monster action's wait ends in the action itself, which need not wait again: it is not waited for.
            foreach (var w in due)
                if (w.Role != PlayerUpdateRole.MonsterAction) { active[(int)w.Role]++; activeTotal++; }
        }
        foreach (var w in due)
        {
            w.Cancel.Dispose();
            // Runs the loop's continuation here, up to its next await.
            long from = Stopwatch.GetTimestamp();
            w.TrySetResult();
            double ms = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
            lock (gate) roleMs[(int)w.Role] += ms;
        }
        lock (gate) stepMs = Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds;
    }

    /// <summary>Waits, at most one frame, for the last step's loops to wait again. A loop that ended or still runs is
    /// let go (counted in the budget's Unsettled).</summary>
    public async Task Settle()
    {
        TaskCompletionSource done;
        lock (gate)
        {
            if (activeTotal == 0 || !Running) return;
            done = settled ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        long from = Stopwatch.GetTimestamp();
        bool all = await Task.WhenAny(done.Task, Task.Delay(Math.Max(1, (int)Program.FrameMs))) == done.Task;
        lock (gate)
        {
            settleMs = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
            if (all) return;
            unsettled = activeTotal;
            unsettledRoles = string.Join(" ", Enumerable.Range(0, Roles).Where(r => active[r] > 0).Select(r => (PlayerUpdateRole)r));
            Array.Clear(active);
            activeTotal = 0;
            settled = null;
        }
    }

    /// <summary>The frame's write: closes its budget entry, logged under Game_TRACE=budget when over a frame.</summary>
    public void Wrote(string remote, int records, int bytes, double writeMs)
    {
        FrameBudget entry;
        lock (gate)
        {
            if (Frame == 0) return;
            entry = new(Frame, Time, stepMs, settleMs, writeMs, records, bytes,
                Monitor.LockContentionCount - contentionAtStep, unsettled, [.. roleMs]);
            ring[ringCount++ % BudgetFrames] = entry;
        }
        if (ServerTrace.Budget && entry.TotalMs > Program.FrameMs)
            Console.WriteLine($"[budget] {remote} frame {entry.Frame} at {entry.Time:0.000} s: {entry.TotalMs:0.0} ms" +
                $" (step {entry.StepMs:0.0}, settle {entry.SettleMs:0.0}{(entry.Unsettled > 0 ? $" with {entry.Unsettled} unsettled: {unsettledRoles}" : "")}," +
                $" write {entry.WriteMs:0.0}), {entry.Records} records {entry.Bytes} B, {entry.Contention} contentions; " +
                string.Join(", ", entry.RoleMs.Select((ms, r) => (ms, r)).Where(p => p.ms > 0).OrderByDescending(p => p.ms).Take(4)
                    .Select(p => $"{(PlayerUpdateRole)p.r} {p.ms:0.0}")));
    }

    /// <summary>The last frames' budgets, oldest first.</summary>
    public FrameBudget[] Budget()
    {
        lock (gate)
        {
            long n = Math.Min(ringCount, BudgetFrames);
            return [.. Enumerable.Range(0, (int)n).Select(i => ring[(ringCount - n + i) % BudgetFrames])];
        }
    }
}

using System.Diagnostics;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The frames of one area: an instance's (InstanceWorld.Frames), shared by every connection in it, or a connection's own
/// outside any instance. One driver steps them on the area's Clock grid (frame n ends at n × FrameMs). At each boundary it
/// waits, at most one frame, for the previous step's loops to settle (each has waited again), ends every member's frame
/// (their records go out, then S→C 0x2b7), and then steps: every loop wait that falls due completes on the driver's own
/// thread, in PlayerUpdateRole order and, within a role, in the order the connections joined. What a step sends goes out in
/// the frame that starts at the boundary, the one its frame time (Time) names. With no member the area has no frames: it
/// stands still until someone comes (InstanceRegistry.Lifetime forgets it 6 minutes after the last one left).
/// </summary>
/// <remarks>
/// A wait is a whole number of frames, rounded to the nearest (at least 1): 16 and 32 ms are 1 frame, 50 ms 2, 100 ms 3,
/// 250 ms 8. The loops integrate the time that passed between their steps, so their per-second rates are kept. While the
/// area has no driver (before the first member's frames start, after the last one left), a wait is a plain delay.
/// Differs from GGG: its server steps its world in frames, so a frame is a tick of the simulation. Ours walks on the clock,
/// so frames are laid over it, and a frame missed by a late timer goes out at once instead of being lost (a free 33 ms
/// timer drifted 28 to 47 ms apart, with gaps to 390 ms; GGG's spacing is 30 to 36 ms). The Clock stops in the Escape
/// menu's pause, and so do the frames.
/// </remarks>
public sealed class AreaFrames(Func<Stopwatch> clock)
{
    public const int BudgetFrames = 256;
    static readonly int Roles = Enum.GetValues<PlayerUpdateRole>().Length;
    // Work longer than this lets the frames go on (logged): no step of ours should take it, and a client held longer looks hung.
    static readonly TimeSpan MaxFrameHold = TimeSpan.FromSeconds(2);

    sealed class Waiter(long due, PlayerUpdateRole role, int member, long order) : TaskCompletionSource(TaskCreationOptions.None)
    {
        public readonly long Due = due, Order = order;
        public readonly int Member = member;
        public readonly PlayerUpdateRole Role = role;
        public CancellationTokenRegistration Cancel;
    }

    sealed class Member(Player conn, string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, double first, int order)
    {
        public readonly Player Conn = conn;
        public readonly string Remote = remote;
        public readonly System.Net.Sockets.NetworkStream Stream = stream;
        public readonly GameServer.Core.Protocol.Login.ConnectionCiphers Ciphers = ciphers;
        public readonly double First = first;
        public readonly int Order = order;
        public double LastEnded = double.NaN;
        // The input phase: the handler's request for its turn, and the turn granted (open until its batches are done).
        public TaskCompletionSource<InputTurn>? Request;
        public InputTurn? Turn;
    }

    /// <summary>A connection's turn in the frame's input phase: its message handler runs the batches it has, then disposes
    /// the turn, and the frame goes on to the next connection's (or, once all are done, to the systems).</summary>
    public sealed class InputTurn : IDisposable
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static readonly InputTurn None = new();
        public void Dispose() => Done.TrySetResult();
    }

    /// <summary>One frame's costs: the step's loops run on the driver (StepMs), the wait for the ones that went on
    /// asynchronously (SettleMs), and the members' frame writes (WriteMs, Records, Bytes). RoleMs[r] is how long the loops of
    /// role r ran on the driver in the step (up to their next await); Contention is the process's lock contentions during it.</summary>
    public readonly record struct FrameBudget(long Frame, double Time, int Members, double StepMs, double SettleMs, double WriteMs,
        int Records, int Bytes, long Contention, int Unsettled, double[] RoleMs, double InputMs = 0, int Inputs = 0, int Spilled = 0)
    {
        public double TotalMs => StepMs + SettleMs + WriteMs + InputMs;
    }

    readonly Lock gate = new();
    readonly List<Waiter> waiting = [];
    readonly List<Member> members = [];
    readonly int[] active = new int[Roles];
    readonly double[] roleMs = new double[Roles];
    readonly FrameBudget[] ring = new FrameBudget[BudgetFrames];
    int activeTotal, unsettled, joined;
    long order, ringCount, stepStarted, contentionAtStep;
    double stepMs, settleMs, writeMs, inputMs;
    int records, bytes, inputs, spilled;
    string unsettledRoles = "";
    TaskCompletionSource? settled;
    Task driver = Task.CompletedTask;

    /// <summary>Frames stepped so far.</summary>
    public long Frame { get; private set; }
    /// <summary>The time on the area's Clock of the last step's boundary, NaN before the first.</summary>
    public double Time { get; private set; } = double.NaN;
    /// <summary>Whether a driver steps the frames (the area has members).</summary>
    public bool Running { get; private set; }

    /// <summary>The area's own world (GameArea), made when the first member joins, with that member's area seed; stepped in
    /// every frame after the input phase, before the loops. Its players are its members' player entities.</summary>
    public GameArea? Area { get; private set; }
    /// <summary>The area's seeded randomness for rules (AreaSeededRandom); the shared one until the first member joins.</summary>
    public Random Rng { get; private set; } = Random.Shared;
    readonly TaskCompletionSource<GameArea> areaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>The area's world, once the first member joined.</summary>
    public Task<GameArea> AreaReady => areaReady.Task;

    static readonly List<WeakReference<AreaFrames>> live = [];

    /// <summary>The areas whose world runs now, with their view (AreaFrames.View): a module that loads adds its systems to
    /// them (GameArea.Defer, at their next frame).</summary>
    public static (GameArea Area, Player View)[] LiveAreas()
    {
        lock (live)
        {
            live.RemoveAll(w => !w.TryGetTarget(out _));
            return [.. live.Select(w => w.TryGetTarget(out var f) ? f : null).OfType<AreaFrames>()
                .Where(f => f.Area is not null && f.View is not null).Select(f => (f.Area!, f.View!))];
        }
    }

    /// <summary>Asks the rules of <paramref name="conn"/>'s league to set up <paramref name="area"/> (GameEvents.AreaStarted).</summary>
    public static void StartRules(GameArea area, Player conn)
    {
        foreach (var (owner, run) in GameEvents.AreaStarted.Subscribers(conn))
            GameEventOwners.Guard(owner, nameof(GameEvents.AreaStarted), () => run(area, conn));
    }

    /// <summary>The frame time for the loops: the last boundary stepped while the frames run, else the Clock.</summary>
    public double Now(Player conn) => Running && !double.IsNaN(Time) ? Time : conn.Clock.Elapsed.TotalSeconds;

    public static int FramesFor(int milliseconds) =>
        Math.Max(1, (int)Math.Round(milliseconds / (double)Math.Max(1, (int)Program.FrameMs), MidpointRounding.AwayFromZero));

    public int MemberCount { get { lock (gate) return members.Count; } }

    /// <summary>The area's players, in the order they joined: its members still in it.</summary>
    public Player[] Players()
    {
        lock (gate) return [.. members.Select(m => m.Conn).Where(c => !c.Closed && !c.LeavingArea && !c.WorldOutput.Closed)];
    }

    /// <summary>The area's view: its first member (in join order) still in it, whose loops step what the area steps once
    /// (its monsters); null while the frames do not run.</summary>
    public Player? View
    {
        get
        {
            lock (gate)
            {
                if (!Running) return null;
                foreach (var m in members)
                    if (!m.Conn.Closed && !m.Conn.LeavingArea && !m.Conn.WorldOutput.Closed) return m.Conn;
                return null;
            }
        }
    }

    // ---- members ----

    /// <summary>The connection's frames start (S→C 0x2b8 first, Program.BeginWorldFrames) and it joins the area's driver,
    /// which starts with the first member.</summary>
    public async Task Join(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, Player conn)
    {
        try
        {
            await Program.Send(remote, stream, ciphers, InstanceProtocol.FrameLength(Program.FrameMs), $"0x02b8 server frame length {Program.FrameMs} ms");
            double first = await Program.BeginWorldFrames(conn, Program.FrameMs / 1000d);
            GameArea? started = null;
            lock (gate)
                if (Area is null) { Rng = new AreaSeededRandom(conn.Seed); areaReady.TrySetResult(Area = started = new GameArea(conn.Seed)); }
            if (started is not null)
            {
                lock (live) live.Add(new(this));
                started.Defer(a => StartRules(a, conn));
            }
            lock (gate)
            {
                if (conn.Closed || members.Any(m => ReferenceEquals(m.Conn, conn))) return;
                members.Add(new(conn, remote, stream, ciphers, first, joined++));
                if (Running) return;
                Running = true;
                if (ExecutionContext.IsFlowSuppressed()) driver = Task.Run(Drive);
                else using (ExecutionContext.SuppressFlow()) driver = Task.Run(Drive);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    int MemberOrder(Player conn)
    {
        foreach (var m in members) if (ReferenceEquals(m.Conn, conn)) return m.Order;
        return int.MaxValue;
    }

    // ---- the input phase ----

    /// <summary>Waits for the connection's turn in the next frame's input phase (at once while it is no member of a running
    /// area). Its handler runs its batches in the turn and disposes it.</summary>
    public Task<InputTurn> Turn(Player conn)
    {
        lock (gate)
        {
            if (!Running || members.FirstOrDefault(m => ReferenceEquals(m.Conn, conn)) is not { } member)
                return Task.FromResult(InputTurn.None);
            return (member.Request ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    // Each member's handler, in join order, runs the messages it was sent since the last frame; the frame waits for each,
    // at most one frame, after which a slow one goes on beside the frame (Spilled) and keeps its turn.
    async Task Input()
    {
        long from = Stopwatch.GetTimestamp();
        foreach (var m in Snapshot())
        {
            InputTurn turn;
            lock (gate)
            {
                if (m.Turn is { } open && !open.Done.Task.IsCompleted) continue;
                m.Turn = null;
                if (m.Request is not { } request) continue;
                m.Request = null;
                m.Turn = turn = new();
                inputs++;
                request.TrySetResult(turn);
            }
            if (await Task.WhenAny(turn.Done.Task, Task.Delay(Math.Max(1, (int)Program.FrameMs))) != turn.Done.Task)
                lock (gate) spilled++;
        }
        lock (gate) inputMs = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
    }

    // ---- loop waits ----

    public Task Wait(Player conn, PlayerUpdateRole role, int milliseconds, CancellationToken stopping)
    {
        if (stopping.IsCancellationRequested) return Task.FromCanceled(stopping);
        Waiter waiter;
        lock (gate)
        {
            if (!Running) return Task.Delay(Math.Max(0, milliseconds), stopping);
            Settled(role);
            waiter = new(Frame + FramesFor(milliseconds), role, MemberOrder(conn), order++);
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

    // ---- the driver ----

    async Task Drive()
    {
        double frame = Program.FrameMs / 1000d;
        var watch = clock();
        double next = Math.Ceiling(watch.Elapsed.TotalSeconds / frame) * frame;
        long pausedStep = 0;
        try
        {
            while (Prune() > 0)
            {
                // Paused (only a member alone in its area pauses), the Clock stands and no frame falls due: it looks again a
                // third of a frame later. The loops still step once a frame length of real time (they see the pause).
                if (Paused() is { } paused)
                {
                    if (paused.Conn.LockstepClient) await Guard(paused, async () => { await Program.EndPausedFrame(paused.Conn); return true; });
                    await Task.Delay(Math.Max(1, Program.FrameMs / 3));
                    if (Stopwatch.GetElapsedTime(pausedStep).TotalMilliseconds >= Program.FrameMs)
                    {
                        pausedStep = Stopwatch.GetTimestamp();
                        Step(BeginStep(watch.Elapsed.TotalSeconds));
                    }
                    continue;
                }
                // Woken at the next frame end, on the Gateway's 1 ms timer (WindowsTimerResolution): a poll of a third of a
                // frame landed just before a boundary often enough to send frames 22 and 44 ms apart.
                double wait = next - watch.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait * 1000)));
                double now = watch.Elapsed.TotalSeconds;
                if (now < next) continue;
                var ended = new List<Member>();
                bool held = false;
                // A frame missed by a late timer goes out now; each one ends after the loops stepped at its start ran.
                for (; next <= now; next += frame)
                {
                    await Settle();
                    BeginBudget();
                    double boundary = next;
                    foreach (var m in Snapshot())
                    {
                        if (boundary < m.First + frame - 1e-9) continue;
                        // Lockstep: the frame ends once the server's running work is done (ServerWork), as a step of GGG's
                        // world ends: a slow step holds the client's world (the "slideshow": it neither moves nor walks on old
                        // orders), and the frames held go out together, which the client plays up to 5 × as fast (07 §6).
                        if (!held && m.Conn.LockstepClient && m.Conn.Work.Busy) { held = true; await HoldFrames(m); }
                        if (await Guard(m, () => Program.EndWorldFrame(m.Conn, (boundary, boundary + frame))))
                        {
                            m.LastEnded = boundary;
                            if (!ended.Contains(m)) ended.Add(m);
                        }
                    }
                    await Input();
                    // The frame's time first: the area's systems and the loops then see the same Now.
                    var due = BeginStep(boundary);
                    await StepArea(boundary);
                    Step(due);
                }
                if (Program.GameLogicOrNull is { } rules)
                    foreach (var m in ended)
                        await Guard(m, async () => { await rules.WorldFrameEnded(m.Conn, m.LastEnded); return true; });
            }
        }
        catch (Exception ex) { Console.WriteLine($"[frames] area driver failed: {ServerLog.Describe(ex)}"); }
        finally { Stop(); }
    }

    // A member whose connection broke leaves; the others go on.
    async Task<bool> Guard(Member m, Func<Task<bool>> write)
    {
        try { return await write(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            lock (gate) members.Remove(m);
            m.Request?.TrySetResult(InputTurn.None);
            return false;
        }
    }

    Member[] Snapshot()
    {
        lock (gate) return [.. members];
    }

    // Leaves the closed members; with none left the driver ends (Running off under the same lock, so a Join starts a new one).
    int Prune()
    {
        Member[] gone;
        int count;
        lock (gate)
        {
            gone = [.. members.Where(m => m.Conn.Closed || m.Conn.WorldOutput.Closed)];
            foreach (var m in gone) members.Remove(m);
            if (members.Count == 0) Running = false;
            count = members.Count;
        }
        // A leaving member's handler does not wait for a turn that will not come.
        foreach (var m in gone) m.Request?.TrySetResult(InputTurn.None);
        return count;
    }

    Member? Paused()
    {
        lock (gate) return members.FirstOrDefault(m => m.Conn.Paused);
    }

    static async Task HoldFrames(Member m)
    {
        long from = Stopwatch.GetTimestamp();
        string? doing = m.Conn.Work.Handling;
        bool done = true;
        try { await m.Conn.Work.Idle.WaitAsync(MaxFrameHold); }
        catch (TimeoutException) { done = false; }
        double ms = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
        if (!done || ms >= 100)
            Console.WriteLine($"[frames] {m.Remote} held {ms:0} ms by the server's work ({doing ?? "no message"})" +
                (done ? "" : ": still running, the frames go on"));
    }

    /// <summary>The driver ends: every waiting loop resumes now, and its next waits are plain delays until a member joins
    /// again.</summary>
    void Stop()
    {
        Waiter[] all;
        lock (gate)
        {
            if (members.Count > 0 && Running) return;
            Running = false;
            all = [.. waiting];
            waiting.Clear();
            Array.Clear(active);
            activeTotal = 0;
            settled?.TrySetResult(); settled = null;
        }
        foreach (var w in all) { w.Cancel.Dispose(); w.TrySetResult(); }
        ReleaseTurns();
    }

    // Without a driver every handler runs at once.
    void ReleaseTurns()
    {
        List<TaskCompletionSource<InputTurn>> requests = [];
        lock (gate)
            foreach (var m in members)
                if (m.Request is { } r) { requests.Add(r); m.Request = null; }
        foreach (var r in requests) r.TrySetResult(InputTurn.None);
    }

    /// <summary>Begins the frame at <paramref name="time"/> (its number and Now) and takes the waits due in it, in role
    /// order, then member order; <see cref="Step"/> completes them, after the area's world stepped.</summary>
    List<Waiter> BeginStep(double time)
    {
        List<Waiter> due = [];
        lock (gate)
        {
            if (!Running) return due;
            Frame++;
            Time = time;
            stepStarted = Stopwatch.GetTimestamp();
            contentionAtStep = Monitor.LockContentionCount;
            Array.Clear(roleMs);
            stepMs = settleMs = 0;
            unsettled = 0;
            for (int i = waiting.Count - 1; i >= 0; i--)
                if (waiting[i].Due <= Frame) { due.Add(waiting[i]); waiting.RemoveAt(i); }
            due.Sort(static (a, b) => a.Role != b.Role ? a.Role.CompareTo(b.Role)
                : a.Member != b.Member ? a.Member.CompareTo(b.Member) : a.Order.CompareTo(b.Order));
            // A monster action's wait ends in the action itself, which need not wait again: it is not waited for.
            foreach (var w in due)
                if (w.Role != PlayerUpdateRole.MonsterAction) { active[(int)w.Role]++; activeTotal++; }
        }
        return due;
    }

    /// <summary>Completes the frame's due waits here, in order: each loop's continuation runs inline up to its next await.</summary>
    void Step(List<Waiter> due)
    {
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
    async Task Settle()
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

    /// <summary>The area's world steps the coming frame (its players first follow the members), and what it wrote for each
    /// player is admitted at once, so it opens the frame that starts at <paramref name="time"/>, before anything else sent
    /// in it.</summary>
    async Task StepArea(double time)
    {
        GameArea? area;
        (EntityId Id, bool In)[] changes;
        Member[] now;
        lock (gate)
        {
            if (!Running || Area is null) return;
            area = Area;
            changes = PlayerChanges();
            now = [.. members];
        }
        try
        {
            foreach (var (id, joined) in changes)
                if (joined) area.Entities.Add(id, EntityKind.Player); else area.Entities.Remove(id);
            area.Step(Frame, time);
        }
        catch (Exception ex) { Console.WriteLine($"[area] frame {Frame} failed: {ServerLog.Describe(ex)}"); }
        foreach (var m in now)
        {
            var records = area.Output.Take(new EntityId(m.Conn.PeerId));
            if (records.Count == 0) continue;
            await Guard(m, async () =>
            {
                foreach (var record in records) await Program.SendAreaRecord(m.Conn, record);
                return true;
            });
        }
        area.Output.Clear();
    }

    // The members that joined or left since the last step, as the area's player entities.
    readonly HashSet<EntityId> players = [];
    (EntityId Id, bool In)[] PlayerChanges()
    {
        var now = members.Where(m => !m.Conn.Closed).Select(m => new EntityId(m.Conn.PeerId)).ToHashSet();
        var changes = now.Except(players).Select(id => (id, true)).Concat(players.Except(now).Select(id => (id, false))).ToArray();
        players.Clear();
        players.UnionWith(now);
        return changes;
    }

    // ---- budget ----

    // Closes the last frame's entry (its step, settle and the writes since) and opens the next one's write totals.
    void BeginBudget()
    {
        FrameBudget entry;
        lock (gate)
        {
            if (Frame == 0) { records = bytes = inputs = spilled = 0; writeMs = inputMs = 0; return; }
            entry = new(Frame, Time, members.Count, stepMs, settleMs, writeMs, records, bytes,
                Monitor.LockContentionCount - contentionAtStep, unsettled, [.. roleMs], inputMs, inputs, spilled);
            ring[ringCount++ % BudgetFrames] = entry;
            records = bytes = inputs = spilled = 0; writeMs = inputMs = 0;
        }
        if (ServerTrace.Budget && entry.TotalMs > Program.FrameMs)
            Console.WriteLine($"[budget] {Label()} frame {entry.Frame} at {entry.Time:0.000} s: {entry.TotalMs:0.0} ms" +
                $" (step {entry.StepMs:0.0}, settle {entry.SettleMs:0.0}{(entry.Unsettled > 0 ? $" with {entry.Unsettled} unsettled: {unsettledRoles}" : "")}," +
                $" write {entry.WriteMs:0.0}, input {entry.InputMs:0.0} for {entry.Inputs}{(entry.Spilled > 0 ? $", {entry.Spilled} spilled" : "")}), {entry.Members} member(s), {entry.Records} records {entry.Bytes} B, {entry.Contention} contentions; " +
                string.Join(", ", entry.RoleMs.Select((ms, r) => (ms, r)).Where(p => p.ms > 0).OrderByDescending(p => p.ms).Take(4)
                    .Select(p => $"{(PlayerUpdateRole)p.r} {p.ms:0.0}")));
    }

    string Label()
    {
        lock (gate) return members.Count == 0 ? "area" : members[0].Remote;
    }

    /// <summary>A member's frame write, counted in the frame's budget.</summary>
    public void Wrote(int frameRecords, int frameBytes, double ms)
    {
        lock (gate) { records += frameRecords; bytes += frameBytes; writeMs += ms; }
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

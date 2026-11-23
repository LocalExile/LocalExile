using System.Collections.Concurrent;
using System.Diagnostics;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The frames of one area: an instance's (InstanceWorld.Frames), shared by every connection in it, or a connection's own
/// outside any instance. One driver steps them on the area's Clock grid (frame n ends at n × FrameMs), and it is the only
/// one that changes the area: every connection's messages, entry and close, every loop and every continuation of game code
/// run in its frame (FrameContext), on its thread. At each boundary it runs what the last step left, handles the messages
/// (Input), ends every member's frame (their records and the area's delta go out, then S→C 0x2b7), steps the area, and
/// completes the loop waits that fall due, in PlayerUpdateRole order and, within a role, in the order the connections came.
/// What a step sends goes out in the frame that starts at the boundary, the one its frame time (Time) names. With no
/// connection the area has no frames: it stands still until someone comes (InstanceRegistry.Lifetime forgets it 6 minutes
/// after the last one left).
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
public sealed class AreaFrames
{
    public const int BudgetFrames = 256;
    static readonly int Roles = Enum.GetValues<PlayerUpdateRole>().Length;

    sealed class Waiter(long due, PlayerUpdateRole role, int member, long order) : TaskCompletionSource(TaskCreationOptions.None)
    {
        public readonly long Due = due, Order = order;
        public readonly int Member = member;
        public readonly PlayerUpdateRole Role = role;
        public CancellationTokenRegistration Cancel;
    }

    sealed class Member(Player player, ClientInbox? inbox, CancellationTokenSource? reading, int order)
    {
        public readonly Player Player = player;
        public readonly ClientInbox? Inbox = inbox;
        public readonly CancellationTokenSource? Reading = reading;
        public readonly int Order = order;
        // Its frames to the client, from Join on.
        public string Remote = "";
        public System.Net.Sockets.NetworkStream Stream = null!;
        public GameServer.Core.Protocol.Login.ConnectionCiphers Ciphers = null!;
        public double First = double.NaN;
        public double LastEnded = double.NaN;
        public bool Framed => !double.IsNaN(First);
        // The message batch its handler is on: one at a time, in the order sent.
        public Task Handling = Task.CompletedTask;
        public bool Detached;
    }

    /// <summary>
    /// The area's frame as a SynchronizationContext: what the game's code awaits continues here, in the frame's pumps on the
    /// driver's thread (<see cref="Pump"/>), and never on another thread. The area's state then has one owner, its frame,
    /// and nothing in it locks.
    /// </summary>
    public sealed class FrameContext(AreaFrames owner) : SynchronizationContext
    {
        readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> posted = new();
        TaskCompletionSource? waiting;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (owner.gate)
            {
                if (owner.running)
                {
                    posted.Enqueue((callback, state));
                    Interlocked.Exchange(ref waiting, null)?.TrySetResult();
                    return;
                }
            }
            // No driver runs this area anymore: run off-frame so posted continuations never strand or wake on a later session.
            ThreadPool.QueueUserWorkItem(static work => work.Callback(work.State), (Callback: callback, State: state), preferLocal: false);
        }

        /// <summary>Completes when something is posted (at once if something waits already).</summary>
        internal Task Posted()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref waiting, signal);
            if (!posted.IsEmpty) signal.TrySetResult();
            return signal.Task;
        }
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Current == this) { callback(state); return; }
            using var done = new ManualResetEventSlim();
            Post(_ => { try { callback(state); } finally { done.Set(); } }, null);
            done.Wait();
        }
        public override SynchronizationContext CreateCopy() => this;
        internal bool TryTake(out (SendOrPostCallback Callback, object? State) work) => posted.TryDequeue(out work);
    }

    readonly FrameContext context;

    public AreaFrames(Func<Stopwatch> clock)
    {
        this.clock = clock;
        context = new(this);
    }

    readonly Func<Stopwatch> clock;

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
    Task driver = Task.CompletedTask;
    volatile bool running;
    long driverGeneration;

    /// <summary>Frames stepped so far.</summary>
    public long Frame { get; private set; }
    /// <summary>The time on the area's Clock of the last step's boundary, NaN before the first.</summary>
    public double Time { get; private set; } = double.NaN;
    /// <summary>Whether a driver steps the frames (the area has members).</summary>
    public bool Running => running;

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
    public static void StartRules(GameArea area, Player player)
    {
        foreach (var (owner, run) in GameEvents.AreaStarted.Subscribers(player))
            GameEventOwners.Guard(owner, nameof(GameEvents.AreaStarted), () => run(area, player));
    }

    /// <summary>The frame time for the loops: the last boundary stepped while the frames run, else the Clock.</summary>
    public double Now(Player player) => Running && !double.IsNaN(Time) ? Time : player.Clock.Elapsed.TotalSeconds;

    public static int FramesFor(int milliseconds) =>
        Math.Max(1, (int)Math.Round(milliseconds / (double)Math.Max(1, (int)Program.FrameMs), MidpointRounding.AwayFromZero));

    public int MemberCount { get { lock (gate) return members.Count; } }

    /// <summary>The area's players, in the order they joined: its members still in it.</summary>
    public Player[] Players()
    {
        lock (gate) return [.. members.Where(m => m.Framed).Select(m => m.Player).Where(c => !c.Closed && !c.LeavingArea && !c.Connection.WorldOutput.Closed)];
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
                    if (m.Framed && !m.Player.Closed && !m.Player.LeavingArea && !m.Player.Connection.WorldOutput.Closed) return m.Player;
                return null;
            }
        }
    }

    // ---- members ----

    /// <summary>The area's world (GameArea), made for the first connection, with its area seed, and set up by its league's rules
    /// in its first frame.</summary>
    public GameArea World(Player player) => seeded ? Area! : Create(player);

    /// <summary>The area's world before anyone came (the instance's own setup, a tool's): the first player seeds it.</summary>
    public GameArea World()
    {
        lock (gate) return Area ??= new GameArea(0);
    }

    volatile bool seeded;

    GameArea Create(Player player)
    {
        GameArea? started = null;
        lock (gate)
            if (!seeded)
            {
                if (Area is null) Area = new GameArea(player.Seed); else Area.Seed = player.Seed;
                Rng = new AreaSeededRandom(player.Seed);
                areaReady.TrySetResult(started = Area);
                seeded = true;
            }
        if (started is not null)
        {
            lock (live) live.Add(new(this));
            started.Defer(a => StartRules(a, player));
        }
        return Area!;
    }

    /// <summary>The connection is in the area from now until <see cref="Detach"/>: its messages (its inbox), its entry, its
    /// loops and its close run in the area's frames, which run while the area has a connection. Its frames to the client
    /// start later (<see cref="Join"/>).</summary>
    public void Attach(Player player, ClientInbox inbox, CancellationTokenSource reading)
    {
        World(player);
        lock (gate)
        {
            members.Add(new(player, inbox, reading, joined++));
            StartDriver();
        }
    }

    /// <summary>The connection has gone: the frames forget it (the driver stops with the last one).</summary>
    public void Detach(Player player)
    {
        lock (gate)
            foreach (var m in members)
                if (ReferenceEquals(m.Player, player)) m.Detached = true;
    }

    /// <summary>The connection's message batch running now, if any.</summary>
    public Task Handling(Player player)
    {
        lock (gate) return members.FirstOrDefault(m => ReferenceEquals(m.Player, player))?.Handling ?? Task.CompletedTask;
    }

    // Under gate.
    void StartDriver()
    {
        if (running) return;
        running = true;
        long generation = ++driverGeneration;
        if (Area is { } area) area.Driven = true;
        if (ExecutionContext.IsFlowSuppressed()) driver = Task.Run(() => Drive(generation));
        else using (ExecutionContext.SuppressFlow()) driver = Task.Run(() => Drive(generation));
    }

    /// <summary>The connection's frames start (S→C 0x2b8 first, Program.BeginWorldFrames): from the next frame end on, the
    /// driver ends its frames.</summary>
    public async Task Join(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, Player player)
    {
        try
        {
            await Program.Send(remote, stream, ciphers, InstanceProtocol.FrameLength(Program.FrameMs), $"0x02b8 server frame length {Program.FrameMs} ms");
            double first = await Program.BeginWorldFrames(player, Program.FrameMs / 1000d);
            World(player);
            lock (gate)
            {
                if (player.Closed) return;
                var member = members.FirstOrDefault(m => ReferenceEquals(m.Player, player));
                if (member is null) members.Add(member = new(player, null, null, joined++));
                if (member.Framed) return;
                (member.Remote, member.Stream, member.Ciphers, member.First) = (remote, stream, ciphers, first);
                StartDriver();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    int MemberOrder(Player player)
    {
        foreach (var m in members) if (ReferenceEquals(m.Player, player)) return m.Order;
        return int.MaxValue;
    }

    // ---- the frame's work ----

    /// <summary>Runs <paramref name="work"/> in the area's frame and completes with it: what a connection does to the game
    /// besides its messages (its entry, its close) goes through here. Never awaited from the frame itself.</summary>
    public Task Run(Func<Task> work) => Run(async () => { await work(); return true; });
    public Task<T> Run<T>(Func<Task<T>> work)
    {
        lock (gate)
            if (running) return Start(context, work);
        return Task.Run(work);
    }
    public Task Run(Action work) => Run(() => { work(); return Task.CompletedTask; });

    /// <summary>Starts <paramref name="work"/> in the frame it is called from, as Task.Run does elsewhere: a skill's own
    /// timeline, a boss's fight. It runs in the frame's next pump, and what it awaits continues in the frame.</summary>
    public static Task Start(Func<Task> work) =>
        SynchronizationContext.Current is FrameContext frame ? Start(frame, async () => { await work(); return true; }) : Task.Run(work);
    public static Task<T> Start<T>(Func<Task<T>> work) =>
        SynchronizationContext.Current is FrameContext frame ? Start(frame, work) : Task.Run(work);
    public static Task Start(Action work) => Start(() => { work(); return Task.CompletedTask; });

    static Task<T> Start<T>(FrameContext frame, Func<Task<T>> work)
    {
        var started = new TaskCompletionSource<Task<T>>();
        var flow = ExecutionContext.Capture();
        frame.Post(_ =>
        {
            if (flow is null) Begin();
            else ExecutionContext.Run(flow, _ => Begin(), null);
        }, null);
        return started.Task.Unwrap();

        void Begin()
        {
            try { started.SetResult(work()); }
            catch (Exception ex) { started.SetResult(Task.FromException<T>(ex)); }
        }
    }

    // Runs work as the frame: on its context, owning the area (GameArea.Owned).
    void OnFrame(Action work)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        var owner = GameArea.Own(Area);
        try { work(); }
        finally
        {
            GameArea.Own(owner);
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // Runs what was posted to the frame (continuations, started work), and what that posts in turn, for at most a frame.
    void Pump()
    {
        long from = Stopwatch.GetTimestamp();
        OnFrame(() =>
        {
            while (context.TryTake(out var work))
            {
                try { work.Callback(work.State); }
                catch (Exception ex) { Console.WriteLine($"[frames] frame work failed: {ServerLog.Describe(ex)}"); }
                if (Stopwatch.GetElapsedTime(from).TotalMilliseconds >= Program.FrameMs) break;
            }
        });
    }

    // What the driver awaits itself (a frame's write) may wait for a send gate the frame's work holds or was just handed:
    // the frame goes on running that work meanwhile, or both would wait for ever.
    async Task Pumping(Task work)
    {
        while (!work.IsCompleted)
        {
            Pump();
            if (work.IsCompleted) break;
            await Task.WhenAny(work, context.Posted());
        }
        await work;
    }

    async Task<T> Pumping<T>(Task<T> work)
    {
        await Pumping((Task)work);
        return await work;
    }

    // The input phase: each connection, in the order they came, handles the message batches it was sent since the last
    // frame, one after another; a batch that waits on something goes on in the pumps, and that connection's next batch
    // waits for it.
    void Input()
    {
        long from = Stopwatch.GetTimestamp();
        OnFrame(() =>
        {
            foreach (var m in Snapshot())
                while (m.Handling.IsCompleted && !m.Player.Closed && m.Inbox is { } inbox && inbox.TryTake(out var batch))
                {
                    inputs++;
                    m.Handling = Program.Handle(m.Player, inbox, batch, m.Reading!);
                }
        });
        Pump();
        inputMs = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
    }

    // ---- loop waits ----

    public Task Wait(Player player, PlayerUpdateRole role, int milliseconds, CancellationToken stopping)
    {
        if (stopping.IsCancellationRequested) return Task.FromCanceled(stopping);
        Waiter waiter;
        lock (gate)
        {
            if (!Running) return Task.Delay(Math.Max(0, milliseconds), stopping);
            Settled(role);
            waiter = new(Frame + FramesFor(milliseconds), role, MemberOrder(player), order++);
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
        activeTotal--;
    }

    /// <summary>A role's loop ended: it will not wait again this step.</summary>
    public void Ended(PlayerUpdateRole role)
    {
        lock (gate) Settled(role);
    }

    // ---- the driver ----

    async Task Drive(long generation)
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
                    // The messages are still handled: the resume (C→S 0x1d4 0) is one of them.
                    Input();
                    if (Paused() is null)
                    {
                        next = Math.Max(next, Math.Ceiling(watch.Elapsed.TotalSeconds / frame) * frame);
                        continue;
                    }
                    if (paused.Player.Connection.LockstepClient) await Pumping(Guard(paused, async () => { await Program.EndPausedFrame(paused.Player); return true; }));
                    await Task.Delay(Math.Max(1, Program.FrameMs / 3));
                    if (Stopwatch.GetElapsedTime(pausedStep).TotalMilliseconds >= Program.FrameMs)
                    {
                        pausedStep = Stopwatch.GetTimestamp();
                        // Keep AreaFrames.Time pinned to the last frame boundary while paused so walkers never step
                        // a fractional mid-frame interval while no lockstep frame ended.
                        var dueNow = BeginStep(double.IsNaN(Time) ? watch.Elapsed.TotalSeconds : Time);
                        OnFrame(() => Step(dueNow));
                        Pump();
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
                // A frame missed by a late timer goes out now; each one ends after the loops stepped at its start ran.
                for (; next <= now; next += frame)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    // What the last step's work still had to do, before its frame ends.
                    Pump();
                    long t1 = Stopwatch.GetTimestamp();
                    BeginBudget();
                    double boundary = next;
                    // The messages that came during the last step are handled before the frame ends, so what they did (a
                    // skill's buff, a stat change) goes out with this end instead of waiting out the next step.
                    Input();
                    if (Paused() is not null) break;
                    long t2 = Stopwatch.GetTimestamp();
                    // A member's first frame: the game puts what its client is missing in the area's frame, and its player
                    // for the others (Program.Joining).
                    var snap = Snapshot();
                    bool anyDue = false;
                    foreach (var m in snap)
                        if (m.Framed && boundary >= m.First + frame - 1e-9)
                        {
                            anyDue = true;
                            if (double.IsNaN(m.LastEnded) && Program.Joining is { } joining)
                                OnFrame(() => joining(m.Player));
                        }
                    // The frame's changes, made once, go to every member with its end. Never drain Area.Delta when no
                    // member is due to receive this frame (e.g. catch-up frames while a slow entry's First is ahead of
                    // boundary), or creates queued into Area.Delta during those frames are lost before the first frame.
                    if (anyDue)
                    {
                        if (Area is { } components) OnFrame(() => { components.Buffs.Publish(); components.Stats.Publish(); components.Life.Publish(); });
                        var delta = Area?.Delta.Take() ?? [];
                        foreach (var m in snap)
                        {
                            if (!m.Framed || boundary < m.First + frame - 1e-9) continue;
                            if (delta.Count > 0 && Program.GameLogicOrNull is { } logic)
                                OnFrame(() => logic.WritingWorldDelta(m.Player, delta));
                            if (await Pumping(Guard(m, () => Program.EndWorldFrame(m.Player, (boundary, boundary + frame), delta))))
                            {
                                m.LastEnded = boundary;
                                if (!ended.Contains(m)) ended.Add(m);
                            }
                        }
                    }
                    long t3 = Stopwatch.GetTimestamp();
                    // The frame's time first: the area's systems and the loops then see the same Now.
                    var due = BeginStep(boundary);
                    await Pumping(StepArea(boundary));
                    long t4 = Stopwatch.GetTimestamp();
                    OnFrame(() => Step(due));
                    Pump();
                    // A frame whose steps took 100 ms or more, by stage: what the client waited for.
                    if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds >= 100)
                        Console.WriteLine($"[frames] slow frame {Frame}: settle {Ms(t0, t1):0} ms, input {Ms(t1, t2):0} ms" +
                            $", frame ends {Ms(t2, t3):0} ms, area {Ms(t3, t4):0} ms (its step {Area?.LastStepMs:0}: {Area?.LastStepParts}), loops {Ms(t4, Stopwatch.GetTimestamp()):0} ms" +
                            $" ({due.Count} due: {string.Join(" ", due.GroupBy(w => w.Role).Select(g => $"{g.Key}x{g.Count()}"))})" +
                            (Area is { Systems.All.Count: > 0 } slow ? "; slowest systems: " + string.Join(", ", slow.Systems.Slowest(3).Select(s => $"{s.Name} {s.LastMs:0} ms")) : ""));
                }
                if (Program.GameLogicOrNull is { } rules && ended.Count > 0)
                {
                    OnFrame(() =>
                    {
                        foreach (var m in ended) _ = Logged(rules.WorldFrameEnded(m.Player, m.LastEnded));
                    });
                    Pump();
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[frames] area driver failed: {ServerLog.Describe(ex)}"); }
        finally { Stop(generation); }
    }

    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    static async Task Logged(Task work)
    {
        try { await work; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        catch (Exception ex) { Console.WriteLine($"[frames] frame end failed: {ServerLog.Describe(ex)}"); }
    }

    // A member whose connection broke leaves; the others go on.
    async Task<bool> Guard(Member m, Func<Task<bool>> write)
    {
        try { return await write(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            m.Player.Connection.CloseReason ??= $"its frame failed to write: {ex.Message}";
            m.Reading?.Cancel();
            return false;
        }
    }

    Member[] Snapshot()
    {
        lock (gate) return [.. members];
    }

    // Leaves the closed members; with none left the driver ends (running off under the same lock, so a Join starts a new one).
    int Prune()
    {
        Member[] gone;
        int count;
        lock (gate)
        {
            gone = [.. members.Where(m => m.Detached || m.Inbox is null && (m.Player.Closed || m.Player.Connection.WorldOutput.Closed))];
            foreach (var m in gone) members.Remove(m);
            if (members.Count == 0) { running = false; if (Area is { } area) area.Driven = false; }
            count = members.Count;
        }
        return count;
    }

    Member? Paused()
    {
        lock (gate) return members.FirstOrDefault(m => m.Framed && m.Player.Paused);
    }

    /// <summary>The driver ends: every waiting loop resumes now, any posted work drains off-frame, and next waits are plain
    /// delays until a member joins again.</summary>
    void Stop(long generation)
    {
        Waiter[] all;
        List<(SendOrPostCallback Callback, object? State)>? drained = null;
        lock (gate)
        {
            if (driverGeneration != generation) return;
            running = false;
            if (Area is { } area) area.Driven = false;
            all = [.. waiting];
            waiting.Clear();
            Array.Clear(active);
            activeTotal = 0;
            while (context.TryTake(out var work)) (drained ??= []).Add(work);
        }
        foreach (var w in all) { w.Cancel.Dispose(); w.TrySetResult(); }
        if (drained is not null)
            foreach (var work in drained)
            {
                try { work.Callback(work.State); }
                catch (Exception ex) { Console.WriteLine($"[frames] frame work failed: {ServerLog.Describe(ex)}"); }
            }
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
            // The loops the last step ran that have not waited again (still awaiting something else).
            unsettled = activeTotal;
            unsettledRoles = string.Join(" ", Enumerable.Range(0, Roles).Where(r => active[r] > 0).Select(r => (PlayerUpdateRole)r));
            Array.Clear(active);
            activeTotal = 0;
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
            // The area's own frame count: the driver's keeps counting while paused, and the area's timers must not run
            // the paused frames' ticks at once on the resume.
            OnFrame(() => area.Step(area.Frame + 1, time));
        }
        catch (Exception ex) { Console.WriteLine($"[area] frame {Frame} failed: {ServerLog.Describe(ex)}"); }
        foreach (var m in now)
        {
            var records = area.Output.Take(new EntityId(m.Player.Id));
            if (records.Count == 0) continue;
            await Guard(m, async () =>
            {
                foreach (var record in records) await Program.SendAreaRecord(m.Player, record);
                return true;
            });
        }
        area.Output.Clear();
    }

    // The members that joined or left since the last step, as the area's player entities.
    readonly HashSet<EntityId> players = [];
    (EntityId Id, bool In)[] PlayerChanges()
    {
        var now = members.Where(m => m.Framed && !m.Player.Closed).Select(m => new EntityId(m.Player.Id)).ToHashSet();
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

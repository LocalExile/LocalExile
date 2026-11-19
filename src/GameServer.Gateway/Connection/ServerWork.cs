/// <summary>The server's work for one connection that is running right now: its message handler's code between two awaits,
/// and the code that code starts without Task.Run (<see cref="ServerWorkContext"/>). A wait (a timer, a send, a frame)
/// is not work, so work never waits for a frame, and a lockstep frame may wait for it (Program.RunWorldFrames): GGG's
/// server steps its world in frames, and a slow step holds the client's world until it is done.</summary>
public sealed class ServerWork
{
    readonly object gate = new();
    int running;
    TaskCompletionSource idle = Done();

    static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>The messages the handler is on (for the log), or null between batches.</summary>
    public volatile string? Handling;

    public bool Busy => Volatile.Read(ref running) > 0;

    /// <summary>Completes when no work runs.</summary>
    public Task Idle { get { lock (gate) return idle.Task; } }

    public void Enter()
    {
        lock (gate)
            if (running++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Exit()
    {
        TaskCompletionSource? done = null;
        lock (gate)
            if (--running == 0) done = idle;
        done?.TrySetResult();
    }
}

/// <summary>Runs a connection's handler continuations on the thread pool, as without a context, and counts each one as
/// <see cref="ServerWork"/> while it runs. Installed by Program.RunHandlers; Task.Run and ConfigureAwait(false) leave it.</summary>
public sealed class ServerWorkContext(ServerWork work) : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) =>
        ThreadPool.QueueUserWorkItem(_ => Run(callback, state));

    public override void Send(SendOrPostCallback callback, object? state) => Run(callback, state);

    public override SynchronizationContext CreateCopy() => this;

    void Run(SendOrPostCallback callback, object? state)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        work.Enter();
        try { callback(state); }
        finally
        {
            work.Exit();
            SetSynchronizationContext(previous);
        }
    }
}

public sealed partial class Player
{
    ServerWork? serverWork;

    /// <summary>This connection's running server work (<see cref="ServerWork"/>). Made on first use: hot reload adds the
    /// field to connections already open, which never ran a constructor that would set it.</summary>
    public ServerWork Work => serverWork ?? Interlocked.CompareExchange(ref serverWork, new(), null) ?? serverWork!;
}

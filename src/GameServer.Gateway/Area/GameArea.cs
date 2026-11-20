using System.Diagnostics;

/// <summary>
/// An area instance's world as one object the area owns: its entities and their data (stores), its timers, events,
/// systems, input and output, and its randomness. Players are entities in it; connections are only the pipes their
/// sessions use. The frame (<see cref="Step"/>) runs on the area's driver (AreaFrames), one area per worker at a time, so
/// nothing in it is shared with another thread and nothing inside it locks. Parallel work inside a frame goes through
/// AreaJobs. Modules add their stores, systems, events, timers and commands with themselves as the source, and Drop removes
/// them all when the module unloads.
/// </summary>
public sealed class GameArea(ulong seed)
{
    public ulong Seed { get; } = seed;
    public readonly Entities Entities = new();
    public readonly AreaEvents Events = new();
    public readonly AreaSystems Systems = new();
    public readonly AreaCommands Commands = new();
    public readonly AreaOutput Output = new();
    public readonly AreaZones Zones = new(new AreaGrid());
    /// <summary>Where the walkers stand, by grid cell (AreaGrid): set by the movement phase each frame.</summary>
    public AreaGrid Grid => Zones.Grid;
    public AreaTimers Timers => timers ??= new(Entities);
    AreaTimers? timers;

    readonly Dictionary<(Type, object), (object Store, object? Source)> stores = [];
    readonly System.Collections.Concurrent.ConcurrentQueue<Action<GameArea>> deferred = new();

    /// <summary>Runs <paramref name="work"/> at the start of the area's next frame, on its frame (any thread may ask): a
    /// module's setup or removal, what another area or a service sends this one.</summary>
    public void Defer(Action<GameArea> work) => deferred.Enqueue(work);
    [ThreadStatic] static GameArea? stepping;

    /// <summary>The frame being stepped (the area's frame count).</summary>
    public long Frame { get; private set; }
    /// <summary>The frame's time on the area's Clock, in seconds.</summary>
    public double Time { get; private set; }
    /// <summary>The last step's length.</summary>
    public double LastStepMs { get; private set; }
    /// <summary>Where the last step's time went, in ms (the slow-frame log).</summary>
    public string LastStepParts { get; private set; } = "";
    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The area's store of <typeparamref name="T"/> under <paramref name="key"/> (made the first time).</summary>
    public Store<T> Store<T>(object key, object? source = null)
    {
        if (stores.TryGetValue((typeof(T), key), out var found)) return (Store<T>)found.Store;
        var store = new Store<T>();
        Entities.Track(store);
        stores[(typeof(T), key)] = (store, source);
        return store;
    }

    /// <summary>Randomness for <paramref name="id"/> in this frame (AreaRandom).</summary>
    public AreaRandom Random(EntityId id, int stream = 0) => AreaRandom.For(Seed, id, Frame, stream);

    /// <summary>One frame: input, timers, then the systems by phase, each one's events delivered after it.</summary>
    public void Step(long frame, double time)
    {
        var previous = stepping;
        stepping = this;
        long start = Stopwatch.GetTimestamp();
        try
        {
            (Frame, Time) = (frame, time);
            while (deferred.TryDequeue(out var work)) work(this);
            long t1 = Stopwatch.GetTimestamp();
            Commands.Apply(this);
            long t2 = Stopwatch.GetTimestamp();
            Systems.Run(this, AreaPhase.Input, AreaPhase.Input);
            long t3 = Stopwatch.GetTimestamp();
            Timers.Update(frame);
            Events.Drain();
            long t4 = Stopwatch.GetTimestamp();
            Systems.Run(this, AreaPhase.Timers);
            LastStepParts = $"deferred {Ms(start, t1):0}, commands {Ms(t1, t2):0}, input systems {Ms(t2, t3):0}, timers {Ms(t3, t4):0}, systems {Ms(t4, Stopwatch.GetTimestamp()):0}";
        }
        finally
        {
            stepping = previous;
            LastStepMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>Whether this thread is stepping this area now.</summary>
    public bool Owned => ReferenceEquals(stepping, this);

    /// <summary>Debug builds check that area state changes only in its frame.</summary>
    [Conditional("DEBUG")]
    public void AssertOwner()
    {
        if (!Owned) throw new InvalidOperationException("Area state was changed outside the area's frame.");
    }

    /// <summary>Removes everything <paramref name="source"/> added: a module unloading, at a frame boundary.</summary>
    public void Drop(object source)
    {
        Systems.Drop(source);
        Events.Drop(source);
        Commands.Drop(source);
        Timers.Drop(source);
        Zones.Drop(source);
        foreach (var (key, value) in stores.ToArray())
            if (ReferenceEquals(value.Source, source))
            {
                stores.Remove(key);
                Entities.Untrack((IEntityStore)value.Store);
            }
    }
}

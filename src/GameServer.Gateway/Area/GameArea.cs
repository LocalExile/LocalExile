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
    /// <summary>The area's seed: the first player's (an area made before anyone came takes theirs then).</summary>
    public ulong Seed { get; internal set; } = seed;
    public readonly Entities Entities = new();
    public readonly AreaEvents Events = new();
    public readonly AreaSystems Systems = new();
    public readonly AreaCommands Commands = new();
    public readonly AreaOutput Output = new();
    /// <summary>The frame's changes to the shared world, written once to every player at its end (AreaDelta).</summary>
    public AreaDelta Delta => field ??= new(this);
    /// <summary>The monsters' Life component: what changed, written at the frame's end (AreaLife).</summary>
    public AreaLife Life => field ??= new(this);
    /// <summary>The entities' Stats component: what changed, written at the frame's start (AreaStats).</summary>
    public AreaStats Stats => field ??= new(this);
    /// <summary>The entities' Buffs component: what changed, written at the frame's start (AreaBuffs).</summary>
    public AreaBuffs Buffs => field ??= new(this);
    /// <summary>What the area wrote of its objects, for a client that joins (AreaObjectRecords).</summary>
    public AreaObjectRecords Objects => field ??= new(this);
    /// <summary>The area was generated (its monsters and objects placed): once, by its first player (GameEvents.AreaGenerated).</summary>
    public bool Generated { get; set; }
    public readonly AreaZones Zones = new(new AreaGrid());
    /// <summary>The area's awake monsters (and the players' minions in it) by object id.</summary>
    public MonsterTable Monsters => field ??= new(this);
    /// <summary>Created, untargetable monsters waiting to emerge: their bodies exist before they rise into Monsters.</summary>
    public readonly SortedDictionary<uint, SpawnedMonster> DormantMonsters = [];
    /// <summary>The area's other monsters waiting to emerge, and its emerge watch.</summary>
    public readonly EmergeGround EmergeGround = new();
    /// <summary>The monster types the area's clients were told (S→C 0x20 "Id@level"; a create of the type needs it).</summary>
    public readonly HashSet<(int Row, byte Level)> MonsterTypes = [];
    /// <summary>Active subsets of <see cref="Monsters"/> with running DoTs, recent ignites, or attacker slows so per-tick
    /// and per-hit checks run in O(active) instead of scanning every monster in the area.</summary>
    public readonly HashSet<uint> RecentlyIgnitedMonsters = [];
    public readonly HashSet<uint> ActiveIgniteMonsters = [];
    public readonly HashSet<uint> ActiveBleedMonsters = [];
    public readonly HashSet<uint> ActivePoisonMonsters = [];
    public readonly HashSet<uint> ActiveDecayMonsters = [];
    public readonly HashSet<uint> ActiveHeraldBurnMonsters = [];
    public readonly HashSet<uint> ActiveAilmentMonsters = [];
    public readonly HashSet<uint> SlowedAttackers = [];
    public bool HasProliferatedAilments;
    public bool HasProliferatedIgnites;
    public bool HasSpiderWebs;
    public bool HasRampageBlinds;

    public bool HasActiveDots =>
        ActiveIgniteMonsters.Count > 0 || ActiveBleedMonsters.Count > 0 || ActivePoisonMonsters.Count > 0 || ActiveDecayMonsters.Count > 0
        || MonsterContagion.AnyApplied || MonsterEssenceDrain.AnyApplied;

    // Order-preserving living-monster cache so 30 Hz movement, collision, blocker, and regen loops do not scan
    // thousands of dead corpses remaining in Monsters for corpse skills.
    readonly List<(uint Id, SpawnedMonster Monster)> livingMonsters = [];
    int livingSyncedCount = -1;
    int livingSyncedVersion = -1;
    public int MonstersVersion;

    /// <summary>Living monsters in ascending object id order. Rebuilt only when Monsters' count or version changes;
    /// dead entries are compacted in place in O(living) with zero allocations.</summary>
    public List<(uint Id, SpawnedMonster Monster)> SyncLivingMonsters()
    {
        int count = Monsters.Count;
        int version = MonstersVersion;
        if (count != livingSyncedCount || version != livingSyncedVersion)
        {
            livingMonsters.Clear();
            foreach (var (id, m) in Monsters)
                if (!m.Dead && m.Life > 0) livingMonsters.Add((id, m));
            livingSyncedCount = count;
            livingSyncedVersion = version;
            return livingMonsters;
        }
        int write = 0;
        int n = livingMonsters.Count;
        for (int i = 0; i < n; i++)
        {
            var entry = livingMonsters[i];
            var m = entry.Monster;
            if (!m.Dead && m.Life > 0)
            {
                if (write != i) livingMonsters[write] = entry;
                write++;
            }
        }
        if (write < n) livingMonsters.RemoveRange(write, n - write);
        return livingMonsters;
    }
    /// <summary>Where the walkers stand, by grid cell (AreaGrid): set by the movement phase each frame, and a monster as it
    /// joins the area.</summary>
    public AreaGrid Grid => Zones.Grid;

    /// <summary>How far a walker may have gone since the movement phase put it in its cell: queries reach this much further,
    /// and the caller keeps its exact test.</summary>
    public const int WalkSlack = 8;

    /// <summary>The living monsters whose body may reach within <paramref name="radius"/> of <paramref name="at"/>, in id
    /// order (as the table's): the few grid cells the radius covers (AreaGrid), never a pass over every monster. A query
    /// for hits, aggro, collision and anything else near a point; the caller keeps its exact distance test.</summary>
    public IEnumerable<KeyValuePair<uint, SpawnedMonster>> MonstersNear((int X, int Y) at, double radius)
    {
        var near = new List<EntityId>();
        Grid.Near(at, (int)Math.Ceiling(radius) + WalkSlack, near);
        near.Sort();
        foreach (var id in near)
            if (Monsters.TryGetValue(id.Value, out var m)) yield return new(id.Value, m);
    }

    public IEnumerable<KeyValuePair<uint, SpawnedMonster>> MonstersNear((double X, double Y) at, double radius) =>
        MonstersNear(((int)Math.Round(at.X), (int)Math.Round(at.Y)), radius + 1);
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

    /// <summary>Whether this thread runs this area's frame now.</summary>
    public bool Owned => ReferenceEquals(stepping, this);

    /// <summary>Whether a driver runs the area's frames (AreaFrames): its state is then the frame's alone.</summary>
    public bool Driven { get; set; }

    /// <summary>This thread runs <paramref name="area"/>'s frame from now (null: none); returns the one it ran before.</summary>
    public static GameArea? Own(GameArea? area)
    {
        var previous = stepping;
        stepping = area;
        return previous;
    }

    /// <summary>Debug builds check that the area's state changes only in its frame, while a driver runs it.</summary>
    [Conditional("DEBUG")]
    public void AssertOwner()
    {
        if (Driven && !Owned) throw new InvalidOperationException("Area state was changed outside the area's frame.");
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

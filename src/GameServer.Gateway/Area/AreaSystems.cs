using System.Diagnostics;

/// <summary>The parts of an area's frame, in the order they run.</summary>
public enum AreaPhase { Input, Timers, Ai, Skills, Movement, Projectiles, Hits, Ailments, Recovery, Stats, Replicate, Bookkeeping }

/// <summary>
/// An area's systems: what its frame runs, by phase, then order, then registration. A system is a plain synchronous
/// function of the area: it reads and changes the area's entities, raises events, adds timers and writes output, and never
/// waits (no sockets, no delays). Heavy ones split their work across the cores (AreaJobs). Each one's time is kept for the
/// budget trace.
/// </summary>
public sealed class AreaSystems
{
    public sealed record AreaSystem(string Name, AreaPhase Phase, int Order, long Seq, Action<GameArea> Run, int Every, object? Source)
    {
        public double LastMs { get; internal set; }
    }

    readonly List<AreaSystem> systems = [];
    long seq;

    public IReadOnlyList<AreaSystem> All => systems;

    /// <summary>Runs <paramref name="run"/> every <paramref name="every"/> frames in <paramref name="phase"/>.</summary>
    public AreaSystem Add(string name, AreaPhase phase, Action<GameArea> run, int order = 0, int every = 1, object? source = null)
    {
        var system = new AreaSystem(name, phase, order, seq++, run, Math.Max(1, every), source);
        systems.Add(system);
        systems.Sort(static (a, b) => a.Phase != b.Phase ? a.Phase.CompareTo(b.Phase)
            : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        return system;
    }

    /// <summary>Runs the systems of <paramref name="from"/> through <paramref name="to"/>, delivering the events each raised
    /// after it.</summary>
    public void Run(GameArea area, AreaPhase from = AreaPhase.Input, AreaPhase to = AreaPhase.Bookkeeping)
    {
        foreach (var system in systems.ToArray())
        {
            if (system.Phase < from || system.Phase > to || area.Frame % system.Every != 0) continue;
            long start = Stopwatch.GetTimestamp();
            system.Run(area);
            area.Events.Drain();
            system.LastMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>The systems that took longest in their last run.</summary>
    public IEnumerable<AreaSystem> Slowest(int count) => systems.OrderByDescending(s => s.LastMs).Take(count);

    public void Drop(object source) => systems.RemoveAll(s => ReferenceEquals(s.Source, source));
}

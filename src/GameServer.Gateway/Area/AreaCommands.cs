using System.Collections.Concurrent;

/// <summary>What a player asked for, parsed and bounds-checked from a client message: the area applies it in its frame's
/// input phase, against its own state.</summary>
public interface IAreaCommand { }

/// <summary>
/// An area's input: sessions post commands from any thread; the frame's input phase applies them, each player's in the
/// order sent, the players in the order they joined. A command nobody handles is dropped and counted.
/// </summary>
public sealed class AreaCommands
{
    interface IHandler
    {
        object? Source { get; }
        void Run(GameArea area, EntityId from, IAreaCommand command);
    }

    sealed class Handler<T>(Action<GameArea, EntityId, T> run, object? source) : IHandler where T : IAreaCommand
    {
        public object? Source => source;
        public void Run(GameArea area, EntityId from, IAreaCommand command) => run(area, from, (T)command);
    }

    readonly record struct Posted(int Player, long Seq, EntityId From, IAreaCommand Command);

    readonly ConcurrentQueue<Posted> queue = new();
    readonly Dictionary<Type, IHandler> handlers = [];
    long seq;

    public long Dropped { get; private set; }
    public int Applied { get; private set; }

    /// <summary>Queues <paramref name="command"/> from <paramref name="from"/> (whose join order is
    /// <paramref name="player"/>). Any thread.</summary>
    public void Post(int player, EntityId from, IAreaCommand command) =>
        queue.Enqueue(new(player, Interlocked.Increment(ref seq), from, command));

    public void On<T>(Action<GameArea, EntityId, T> handler, object? source = null) where T : IAreaCommand =>
        handlers[typeof(T)] = new Handler<T>(handler, source);

    /// <summary>The input phase: applies what was posted before it began.</summary>
    public void Apply(GameArea area)
    {
        if (queue.IsEmpty) { Applied = 0; return; }
        var batch = new List<Posted>();
        while (queue.TryDequeue(out var posted)) batch.Add(posted);
        batch.Sort(static (a, b) => a.Player != b.Player ? a.Player.CompareTo(b.Player) : a.Seq.CompareTo(b.Seq));
        Applied = 0;
        foreach (var p in batch)
        {
            if (!handlers.TryGetValue(p.Command.GetType(), out var handler)) { Dropped++; continue; }
            handler.Run(area, p.From, p.Command);
            Applied++;
            area.Events.Drain();
        }
    }

    public void Drop(object source)
    {
        foreach (var (type, handler) in handlers.ToArray())
            if (ReferenceEquals(handler.Source, source)) handlers.Remove(type);
    }
}

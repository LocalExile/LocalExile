/// <summary>
/// An area's events: a system raises one (a kill, a buff added, a condition that flipped) and every handler of its type
/// runs, in their order, the same frame. Events wait in a queue and are delivered after the system that raised them
/// (AreaSystems.Run drains it after each system and phase), in the order they were raised; a handler may raise more,
/// delivered in the same drain, up to <see cref="MaxPerDrain"/> (the rest go on in the next drain, logged).
/// </summary>
public sealed class AreaEvents
{
    sealed record Handler(int Order, long Seq, Delegate Run, object? Source);

    readonly Dictionary<Type, List<Handler>> handlers = [];
    readonly Queue<Action> pending = new();
    long seq;

    public int MaxPerDrain { get; set; } = 100_000;
    public int Pending => pending.Count;

    /// <summary>Handles every <typeparamref name="T"/> raised; lower <paramref name="order"/> first, then registration order.</summary>
    public void On<T>(Action<T> handler, object? source = null, int order = 0)
    {
        if (!handlers.TryGetValue(typeof(T), out var list)) handlers[typeof(T)] = list = [];
        list.Add(new(order, seq++, handler, source));
        list.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
    }

    public void Raise<T>(T e)
    {
        if (!handlers.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
        pending.Enqueue(() => { foreach (var h in list.ToArray()) ((Action<T>)h.Run)(e); });
    }

    /// <summary>Delivers the queued events. Returns how many.</summary>
    public int Drain()
    {
        int n = 0;
        while (n < MaxPerDrain && pending.TryDequeue(out var deliver)) { deliver(); n++; }
        if (pending.Count > 0) Console.WriteLine($"[area] {pending.Count} events left for the next drain (over {MaxPerDrain} in one)");
        return n;
    }

    /// <summary>Removes every handler <paramref name="source"/> registered (a module unloading).</summary>
    public void Drop(object source)
    {
        foreach (var list in handlers.Values) list.RemoveAll(h => ReferenceEquals(h.Source, source));
    }
}

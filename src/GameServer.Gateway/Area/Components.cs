/// <summary>What modules keep on a player or an area (a component), by slot: a store reads its value by index, no hashing.
/// A slot is given out once per store for the process, so a reloaded module's stores never meet the old ones' values.</summary>
public sealed class Components
{
    static int slots;
    object?[] values = [];

    public static int NewSlot() => Interlocked.Increment(ref slots) - 1;

    public object? Get(int slot)
    {
        var all = Volatile.Read(ref values);
        return slot < all.Length ? all[slot] : null;
    }

    public void Set(int slot, object? value)
    {
        lock (this)
        {
            if (slot >= values.Length)
            {
                var grown = new object?[Math.Max(slot + 1, values.Length * 2)];
                values.CopyTo(grown, 0);
                Volatile.Write(ref values, grown);
            }
            values[slot] = value;
        }
    }

    /// <summary>The value in the slot, made by <paramref name="create"/> the first time.</summary>
    public T GetOrAdd<T>(int slot, Func<T> create) where T : class?
    {
        if (Get(slot) is T found) return found;
        lock (this)
        {
            if (Get(slot) is T raced) return raced;
            var made = create();
            Set(slot, made);
            return made;
        }
    }
}

/// <summary>Something modules keep components on: a player, an area.</summary>
public interface IHasComponents
{
    Components Components { get; }
}

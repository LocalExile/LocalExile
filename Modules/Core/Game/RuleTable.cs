using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

/// <summary>The rules' weak table of something kept per connection, instance or other server object, which outlives this
/// module. In a plain <see cref="ConditionalWeakTable{TKey, TValue}"/> an entry lives as long as its key, and its value
/// keeps the module that made it in memory after an unload, until the player leaves the area or the instance goes. A
/// reload starts this state over anyway (the new rules have tables of their own), so the module empties its tables as
/// the last step of its unload (<see cref="RuleTables.Empty"/>), and code of it still finishing then stores nothing
/// more.</summary>
public sealed class RuleTable<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, RuleTables.IEmptied
    where TKey : class where TValue : class?
{
    readonly ConditionalWeakTable<TKey, TValue> table = new();
    volatile bool emptied;

    public RuleTable() => RuleTables.Register(this);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!emptied) return table.TryGetValue(key, out value);
        value = default;
        return false;
    }

    public TValue GetValue(TKey key, ConditionalWeakTable<TKey, TValue>.CreateValueCallback create) =>
        emptied ? create(key) : Kept(key, table.GetValue(key, create));

    public TValue GetOrCreateValue(TKey key) => emptied ? Activator.CreateInstance<TValue>() : Kept(key, table.GetOrCreateValue(key));

    public void Add(TKey key, TValue value)
    {
        if (emptied) return;
        table.Add(key, value);
        Kept(key, value);
    }

    public void AddOrUpdate(TKey key, TValue value)
    {
        if (emptied) return;
        table.AddOrUpdate(key, value);
        Kept(key, value);
    }

    public bool Remove(TKey key) => table.Remove(key);

    public void Clear() => table.Clear();

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() =>
        emptied ? Enumerable.Empty<KeyValuePair<TKey, TValue>>().GetEnumerator() : ((IEnumerable<KeyValuePair<TKey, TValue>>)table).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void RuleTables.IEmptied.Empty()
    {
        emptied = true;
        table.Clear();
    }

    // An entry stored while the module unloaded goes again: the flag is set before the table is emptied.
    TValue Kept(TKey key, TValue value)
    {
        if (emptied) table.Remove(key);
        return value;
    }
}

/// <summary>The rule tables of this load of the module.</summary>
public static class RuleTables
{
    public interface IEmptied
    {
        void Empty();
    }

    static readonly List<WeakReference<IEmptied>> tables = [];
    static bool emptied;

    internal static void Register(IEmptied table)
    {
        lock (tables)
        {
            if (!emptied)
            {
                tables.RemoveAll(t => !t.TryGetTarget(out _));
                tables.Add(new(table));
                return;
            }
        }
        table.Empty();
    }

    /// <summary>Empties every rule table, and those made afterwards. The last step of the module's unload
    /// (GameModule.Dispose): what the module's lifetime owns is released before, and those clean-ups read the tables to
    /// stop what is in them (a boss fight's <c>CleanupOnUnload</c>).</summary>
    public static void Empty()
    {
        List<IEmptied> all = [];
        lock (tables)
        {
            emptied = true;
            foreach (var weak in tables)
                if (weak.TryGetTarget(out var table)) all.Add(table);
            tables.Clear();
        }
        foreach (var table in all) table.Empty();
    }
}

/// <summary>The rules' store of something kept on a player or an area (a component in its <see cref="Components"/>): read
/// by slot, with <see cref="RuleTable{TKey, TValue}"/>'s calls. Emptied with the module's unload (<see cref="RuleTables.Empty"/>),
/// which takes its values off every player and area that has one.</summary>
public sealed class Store<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, RuleTables.IEmptied
    where TKey : class, IHasComponents where TValue : class?
{
    readonly int slot = Components.NewSlot();
    readonly List<WeakReference<TKey>> holders = [];
    volatile bool emptied;

    public Store() => RuleTables.Register(this);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!emptied && key.Components.Get(slot) is TValue found) { value = found; return true; }
        value = default;
        return false;
    }

    public TValue GetValue(TKey key, Func<TKey, TValue> create)
    {
        if (emptied) return create(key);
        bool made = false;
        var value = key.Components.GetOrAdd(slot, () => { made = true; return create(key); });
        if (made) Held(key);
        return value;
    }

    public TValue GetOrCreateValue(TKey key) => GetValue(key, _ => Activator.CreateInstance<TValue>());

    public void Add(TKey key, TValue value)
    {
        if (key.Components.Get(slot) is not null) throw new ArgumentException("The key already has a value.");
        AddOrUpdate(key, value);
    }

    public void AddOrUpdate(TKey key, TValue value)
    {
        if (emptied) return;
        key.Components.Set(slot, value);
        Held(key);
    }

    public bool Remove(TKey key)
    {
        bool had = key.Components.Get(slot) is not null;
        key.Components.Set(slot, null);
        return had;
    }

    public void Clear()
    {
        lock (holders)
        {
            foreach (var weak in holders)
                if (weak.TryGetTarget(out var key)) key.Components.Set(slot, null);
            holders.Clear();
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        List<KeyValuePair<TKey, TValue>> all = [];
        lock (holders)
            foreach (var weak in holders)
                if (weak.TryGetTarget(out var key) && key.Components.Get(slot) is TValue value) all.Add(new(key, value));
        return all.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void Held(TKey key)
    {
        lock (holders)
        {
            if (holders.Count > 64 && holders.Count % 64 == 0) holders.RemoveAll(w => !w.TryGetTarget(out _));
            holders.Add(new(key));
        }
        if (emptied) key.Components.Set(slot, null);
    }

    void RuleTables.IEmptied.Empty()
    {
        emptied = true;
        Clear();
    }
}

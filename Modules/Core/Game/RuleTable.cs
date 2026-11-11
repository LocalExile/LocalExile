using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

/// <summary>The rules' weak table of something kept per connection, instance or other server object, which outlives this
/// module. In a plain <see cref="ConditionalWeakTable{TKey, TValue}"/> an entry lives as long as its key, and its value
/// keeps the module that made it in memory after an unload, until the player leaves the area or the instance goes. A
/// reload starts this state over anyway (the new rules have tables of their own), so the module empties its tables when
/// it unloads (<see cref="RuleTables.EmptyOnUnload"/>), and code of it still finishing then stores nothing more.</summary>
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

    /// <summary>Empties every rule table, and those made afterwards, when the module's lifetime owning it stops.</summary>
    public static IDisposable EmptyOnUnload() => new Unload();

    sealed class Unload : IDisposable
    {
        public void Dispose()
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
}

/// <summary>An object of an area: the id the client knows it by (players 0x20000000 on, the instance's monsters 0x08000000
/// on, server objects 0x04000000 on).</summary>
public readonly record struct EntityId(uint Value) : IComparable<EntityId>
{
    public int CompareTo(EntityId other) => Value.CompareTo(other.Value);
    public override string ToString() => $"0x{Value:x}";
}

public enum EntityKind : byte { Player, Monster, Minion, Projectile, Object }

/// <summary>The entities an area has. Removing one empties every store of it (Store&lt;T&gt;) and lets its timers lapse
/// (AreaTimers): what belonged to it goes with it.</summary>
public sealed class Entities
{
    readonly Dictionary<EntityId, EntityKind> kinds = [];
    readonly List<IEntityStore> stores = [];

    public int Count => kinds.Count;
    public bool Exists(EntityId id) => kinds.ContainsKey(id);
    public EntityKind? KindOf(EntityId id) => kinds.TryGetValue(id, out var kind) ? kind : null;

    public void Add(EntityId id, EntityKind kind) => kinds[id] = kind;

    public bool Remove(EntityId id)
    {
        if (!kinds.Remove(id)) return false;
        foreach (var store in stores) store.Remove(id);
        return true;
    }

    /// <summary>The ids of one kind (or all), in id order: the order every system walks them in.</summary>
    public EntityId[] Ids(EntityKind? kind = null)
    {
        var ids = kind is { } k ? kinds.Where(p => p.Value == k).Select(p => p.Key).ToArray() : [.. kinds.Keys];
        Array.Sort(ids);
        return ids;
    }

    internal void Track(IEntityStore store) => stores.Add(store);
    internal void Untrack(IEntityStore store) => stores.Remove(store);
}

interface IEntityStore
{
    bool Remove(EntityId id);
}

/// <summary>One kind of data per entity (its life pool, its buffs, its walk): a component store of the area. A module's
/// store is dropped with the module (Area.Drop).</summary>
public sealed class Store<T> : IEntityStore
{
    readonly Dictionary<EntityId, T> values = [];

    public int Count => values.Count;
    public bool Has(EntityId id) => values.ContainsKey(id);
    public T? Get(EntityId id) => values.GetValueOrDefault(id);
    public bool TryGet(EntityId id, out T value) => values.TryGetValue(id, out value!);
    public void Set(EntityId id, T value) => values[id] = value;
    public bool Remove(EntityId id) => values.Remove(id);

    /// <summary>The value, made by <paramref name="create"/> the first time.</summary>
    public T GetOrAdd(EntityId id, Func<EntityId, T> create)
    {
        if (!values.TryGetValue(id, out var value)) values[id] = value = create(id);
        return value;
    }

    /// <summary>The ids that have a value, in id order.</summary>
    public EntityId[] Ids()
    {
        var ids = values.Keys.ToArray();
        Array.Sort(ids);
        return ids;
    }

    public IEnumerable<KeyValuePair<EntityId, T>> All => values;
}

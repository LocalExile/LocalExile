using System.Collections.Concurrent;

/// <summary>
/// A world's awake monsters, by id. A monster put here has its stat changes listed for the world's Stats phase
/// (<see cref="TakeMarked"/>), so the phase steps only the monsters a change marked, never all of them.
/// </summary>
public sealed class MonsterTable : SortedDictionary<uint, SpawnedMonster>
{
    readonly ConcurrentDictionary<SpawnedMonster, byte> marked = new(ReferenceEqualityComparer.Instance);

    public new SpawnedMonster this[uint id]
    {
        get => base[id];
        set { base[id] = value; Join(value); }
    }

    public new void Add(uint id, SpawnedMonster monster) { base.Add(id, monster); Join(monster); }

    public bool TryAdd(uint id, SpawnedMonster monster)
    {
        if (ContainsKey(id)) return false;
        Add(id, monster);
        return true;
    }

    public new bool Remove(uint id) => Remove(id, out _);

    public bool Remove(uint id, out SpawnedMonster monster)
    {
        if (!TryGetValue(id, out monster!) || !base.Remove(id)) return false;
        marked.TryRemove(monster, out _);
        return true;
    }

    public new void Clear() { base.Clear(); marked.Clear(); }

    // Its marks come here from now on, and a monster new to the table is stepped once (its first build).
    void Join(SpawnedMonster monster)
    {
        monster.StatsMarked = marked;
        marked.TryAdd(monster, 0);
    }

    /// <summary>The monsters whose stats a change marked since the last call, taken off the list.</summary>
    public List<SpawnedMonster> TakeMarked()
    {
        var taken = new List<SpawnedMonster>(marked.Count);
        foreach (var monster in marked.Keys)
            if (marked.TryRemove(monster, out _)) taken.Add(monster);
        return taken;
    }
}

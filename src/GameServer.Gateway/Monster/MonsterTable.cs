using System.Collections.Concurrent;

/// <summary>
/// An area's awake monsters, by id: the area's frame owns them (Debug builds check that only it adds or removes one). A
/// monster put here has its stat changes listed for the world's Stats phase (<see cref="TakeMarked"/>), so the phase steps
/// only the monsters a change marked, never all of them.
/// </summary>
public sealed class MonsterTable(GameArea area) : SortedDictionary<uint, SpawnedMonster>
{
    readonly ConcurrentDictionary<SpawnedMonster, byte> marked = new(ReferenceEqualityComparer.Instance);

    // Adding, replacing or removing a body changes which monsters are alive, so the living-monster cache
    // (GameArea.SyncLivingMonsters) must rebuild even when the table's count comes back to the same value.
    public new SpawnedMonster this[uint id]
    {
        get => base[id];
        set { area.AssertOwner(); base[id] = value; Join(id, value); area.MonstersVersion++; }
    }

    public new void Add(uint id, SpawnedMonster monster) { area.AssertOwner(); base.Add(id, monster); Join(id, monster); area.MonstersVersion++; }

    public bool TryAdd(uint id, SpawnedMonster monster)
    {
        if (ContainsKey(id)) return false;
        Add(id, monster);
        return true;
    }

    public new bool Remove(uint id) => Remove(id, out _);

    public bool Remove(uint id, out SpawnedMonster monster)
    {
        area.AssertOwner();
        if (!TryGetValue(id, out monster!) || !base.Remove(id)) return false;
        marked.TryRemove(monster, out _);
        area.Grid.Remove(new(id));
        area.MonstersVersion++;
        return true;
    }

    public new void Clear()
    {
        area.AssertOwner();
        foreach (var id in Keys) area.Grid.Remove(new(id));
        base.Clear();
        marked.Clear();
        area.MonstersVersion++;
    }

    // Its marks come here from now on, a monster new to the table is stepped once (its first build), and it is in the
    // area's grid at once, not only from the next movement phase.
    void Join(uint id, SpawnedMonster monster)
    {
        if (!monster.Dead) area.Grid.Set(new(id), monster.Walk.Position(area.Time), monster.StopRange);
        monster.StatsMarked = marked;
        monster.LifeChanged = () => area.Life.Changed(id);
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

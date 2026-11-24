using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The Stats component of the area's entities (S→C 0x1b3: the stats the client keeps of an object). Systems set an
/// entity's stats: its whole list (a player's final stats from its stats step) or single stats (a monster's
/// hide_mini_life_bar, a curse's stats). The frame's end writes one 0x1b3 per entity with only the stats that differ from
/// what the area wrote the frame before, a stat that left the whole list as 0, in the frame's first phase: an action
/// started in the frame reads them (the client reads attack and cast speed at the action's start, 0x141b58740).
/// </summary>
public sealed class AreaStats(GameArea area)
{
    /// <summary>A stats record was written for an entity (the game relays a lockstep walk on its movement stats).</summary>
    public static Action<GameArea, uint, IReadOnlyList<(uint Key, int Value)>>? Written;

    readonly Dictionary<uint, Dictionary<uint, int>> written = [];
    // The keys of each entity's last whole list: a key missing from the next one is cleared.
    readonly Dictionary<uint, HashSet<uint>> listed = [];
    readonly ConcurrentDictionary<uint, IReadOnlyList<(uint Key, int Value)>> lists = new();
    readonly ConcurrentDictionary<(uint Id, uint Key), int> single = new();
    readonly ConcurrentDictionary<uint, IReadOnlyList<(uint Key, int Value)>> created = new();

    /// <summary>The stats the entity's create carried: later changes are written against them.</summary>
    public void Created(uint id, IReadOnlyList<(uint Key, int Value)> stats) => created[id] = stats;

    /// <summary>Whether the area has recorded baseline or published stats for <paramref name="id"/>.</summary>
    public bool HasWritten(uint id) => written.ContainsKey(id) || created.ContainsKey(id);

    /// <summary>The last published (or creation-baseline) value of <paramref name="key"/> for <paramref name="id"/>, or 0.</summary>
    public int WrittenValue(uint id, uint key)
    {
        if (written.TryGetValue(id, out var values)) return values.GetValueOrDefault(key);
        if (created.TryGetValue(id, out var initial))
        {
            int found = 0;
            for (int i = 0; i < initial.Count; i++)
                if (initial[i].Key == key) found = initial[i].Value;
            return found;
        }
        return 0;
    }

    /// <summary>The entity's whole list of stats now; what its last list had and this one has not goes to 0.</summary>
    public void SetAll(uint id, IReadOnlyList<(uint Key, int Value)> stats) => lists[id] = stats;

    public void Set(uint id, uint key, int value) => single[(id, key)] = value;

    /// <summary>An entity left the area: a later object with its id starts from nothing.</summary>
    public void Removed(uint id)
    {
        area.AssertOwner();
        written.Remove(id); listed.Remove(id); lists.TryRemove(id, out _); created.TryRemove(id, out _);
        foreach (var key in single.Keys.Where(k => k.Id == id).ToList()) single.TryRemove(key, out _);
    }

    /// <summary>A client that joins the area is written the stats the area wrote of its entities since their creates.</summary>
    public void Join(Player player)
    {
        area.AssertOwner();
        foreach (var (id, values) in written)
            if (values.Count > 0) area.Delta.Add(id, InstanceEntities.StatsUpdate(id, [.. values.Select(v => (v.Key, v.Value))]), null, only: player);
    }

    public void Publish()
    {
        area.AssertOwner();
        foreach (var id in created.Keys)
        {
            if (!created.TryRemove(id, out var stats)) continue;
            var values = written[id] = [];
            foreach (var (key, value) in stats) values[key] = value;
            listed[id] = [.. values.Keys];
        }
        var changes = new Dictionary<uint, List<(uint Key, int Value)>>();
        foreach (var id in lists.Keys)
        {
            if (!lists.TryRemove(id, out var stats)) continue;
            var keys = new HashSet<uint>(stats.Count);
            foreach (var (key, value) in stats) { keys.Add(key); Change(changes, id, key, value); }
            if (listed.TryGetValue(id, out var before))
                foreach (var key in before)
                    if (!keys.Contains(key)) Change(changes, id, key, 0);
            listed[id] = keys;
        }
        foreach (var (id, key) in single.Keys)
            if (single.TryRemove((id, key), out int value)) Change(changes, id, key, value);
        foreach (var (id, stats) in changes)
        {
            area.Delta.Add(id, InstanceEntities.StatsUpdate(id, stats));
            Written?.Invoke(area, id, stats);
        }
    }

    void Change(Dictionary<uint, List<(uint, int)>> changes, uint id, uint key, int value)
    {
        if (!written.TryGetValue(id, out var values)) written[id] = values = [];
        if (values.TryGetValue(key, out int was) ? was == value : value == 0 && listed.ContainsKey(id)) return;
        values[key] = value;
        if (!changes.TryGetValue(id, out var list)) changes[id] = list = [];
        list.Add((key, value));
    }
}

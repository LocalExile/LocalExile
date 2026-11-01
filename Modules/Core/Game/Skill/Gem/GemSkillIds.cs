

/// <summary>
/// The skill ids of one character's socketed gems. The client binds skill bar keys by skill id, so each gem keeps its id
/// while it stays socketed, and other gems coming or going never change it: a gem that stays in its socket keeps its id,
/// a gem moved to another socket keeps it too (matched by its skill), even through the cursor, where it is out of the
/// equipment for an update or more, and a new gem takes the lowest id no gem had. A Vaal gem has two skills in its
/// socket (its Vaal skill and its regular skill), each with its own id.
/// </summary>
sealed class GemSkillIds
{
    /// <summary>A socketed gem: the item it is in, its socket, its GrantedEffects row.</summary>
    public readonly record struct Gem(uint Host, int Socket, int Effect);

    Dictionary<(uint Host, int Socket, int Effect), (ushort Id, int Effect)> _byPlace = [];
    // The gems that left the equipment, oldest first: their ids wait for a gem of their skill to come back (a gem moved
    // through the cursor), and go to a new gem only when no id is left that no gem had.
    List<(ushort Id, int Effect)> _left = [];
    readonly Lock _lock = new();

    /// <summary>The ids of <paramref name="gems"/>, in their order, from <paramref name="pool"/> (the ids gem skills may
    /// use, in the order to hand them out); null for a gem when the pool has no free id left. An id that a gem gives up
    /// never goes to another skill's gem in the same call: the skill must first go out as removed, so its aura or
    /// Righteous Fire ends and the skill bar key bound to it does not cast the new gem (a gem swapped into a socket).</summary>
    public IReadOnlyList<ushort?> Assign(IReadOnlyList<Gem> gems, IReadOnlyList<ushort> pool)
    {
        lock (_lock)
        {
            var ids = new ushort?[gems.Count];
            var used = new HashSet<ushort>();
            // A gem in the same socket with the same skill keeps its id.
            for (int i = 0; i < gems.Count; i++)
                if (_byPlace.TryGetValue((gems[i].Host, gems[i].Socket, gems[i].Effect), out var had) && had.Effect == gems[i].Effect && used.Add(had.Id))
                    ids[i] = had.Id;
            // A gem moved to another socket keeps the id of the same skill's gem that left its place: in this call, or
            // earlier (it went through the cursor).
            var leaving = _byPlace.Values.Where(p => !used.Contains(p.Id)).ToList();
            var candidates = leaving.Concat(_left).ToList();
            for (int i = 0; i < gems.Count; i++)
                if (ids[i] is null && candidates.FindIndex(l => l.Effect == gems[i].Effect) is var at and >= 0)
                {
                    ids[i] = candidates[at].Id;
                    used.Add(candidates[at].Id);
                    candidates.RemoveAt(at);
                }
            // A new gem takes the lowest id no gem had, else the id of the gem that left longest ago. Never an id given up
            // in this call.
            var reserved = _byPlace.Values.Select(p => p.Id).Concat(_left.Select(l => l.Id)).ToHashSet();
            var spare = _left.Where(l => !used.Contains(l.Id)).Select(l => l.Id).ToList();
            for (int i = 0; i < gems.Count; i++)
            {
                if (ids[i] is not null) continue;
                if (pool.FirstOrDefault(id => !used.Contains(id) && !reserved.Contains(id)) is var free and not 0) ids[i] = free;
                else if (spare.FirstOrDefault(id => !used.Contains(id)) is var old and not 0) ids[i] = old;
                if (ids[i] is ushort id) used.Add(id);
            }
            _left = [.. _left.Concat(leaving).Where(l => !used.Contains(l.Id)).DistinctBy(l => l.Id)];
            _byPlace = [];
            for (int i = 0; i < gems.Count; i++)
                if (ids[i] is ushort id) _byPlace[(gems[i].Host, gems[i].Socket, gems[i].Effect)] = (id, gems[i].Effect);
            return ids;
        }
    }
}

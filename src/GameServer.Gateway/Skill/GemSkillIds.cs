

/// <summary>
/// The skill ids of one character's socketed gems. The client binds skill bar keys by skill id, so each gem keeps its id
/// while it stays socketed, and other gems coming or going never change it: a gem that stays in its socket keeps its id,
/// a gem moved to another socket keeps it too (matched by its skill), even through the cursor, where it is out of the
/// equipment for an update or more, and a new gem takes the lowest id no gem had. A Vaal gem has two skills in its
/// socket (its Vaal skill and its regular skill), each with its own id.
/// </summary>
public sealed class GemSkillIds
{
    public enum SourceKind { SocketedGem, PassiveGrant, WornGrant }

    // Neutral identity state must outlive a Game module replacement: a live actor holds these skill ids.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GemSkillIds> ByCharacter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One source that may grant a skill: its source kind, host, source-local socket and GrantedEffects row. Socketed
    /// gems and passive nodes may inherit an id by skill identity when they move; an item grant stays tied to its
    /// exact host/mod row and cannot take an id from a distinct host.
    /// </summary>
    public readonly record struct Gem(uint Host, int Socket, int Effect, SourceKind Source = SourceKind.SocketedGem);
    readonly record struct Left(ushort Id, Gem Key);

    Dictionary<(SourceKind Source, uint Host, int Socket, int Effect), (ushort Id, int Effect)> _byPlace = [];
    // The gems that left the equipment, oldest first: their ids wait for a gem of their skill to come back (a gem moved
    // through the cursor), and go to a new gem only when no id is left that no gem had.
    List<Left> _left = [];
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
                if (_byPlace.TryGetValue((gems[i].Source, gems[i].Host, gems[i].Socket, gems[i].Effect), out var had)
                    && had.Effect == gems[i].Effect && used.Add(had.Id))
                    ids[i] = had.Id;
            // A socketed gem moved to another socket keeps the id of the same skill's gem that left its place: in this
            // call, or earlier (it went through the cursor). Passive and worn sources only return to the same host
            // identity; a same-effect source on another item is a different skill instance.
            var leaving = _byPlace.Where(p => !used.Contains(p.Value.Id))
                .Select(p => new Left(p.Value.Id, new(p.Key.Host, p.Key.Socket, p.Key.Effect, p.Key.Source))).ToList();
            var candidates = leaving.Concat(_left).ToList();
            for (int i = 0; i < gems.Count; i++)
                if (ids[i] is null && candidates.FindIndex(l => CanInherit(l.Key, gems[i])) is var at and >= 0)
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
                if (ids[i] is ushort id) _byPlace[(gems[i].Source, gems[i].Host, gems[i].Socket, gems[i].Effect)] = (id, gems[i].Effect);
            return ids;
        }
    }

    static bool CanInherit(Gem previous, Gem current) =>
        previous.Effect == current.Effect && previous.Source == current.Source && current.Source switch
        {
            SourceKind.SocketedGem => true,
            SourceKind.PassiveGrant => previous.Host == current.Host && previous.Socket == current.Socket,
            SourceKind.WornGrant => previous.Host == current.Host && previous.Socket == current.Socket,
            _ => false,
        };
}

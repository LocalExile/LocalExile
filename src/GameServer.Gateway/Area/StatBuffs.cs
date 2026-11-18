/// <summary>
/// Buffs on an area's entity that add stats while it is in that area, for as long as their owner keeps them (no
/// duration); a new area starts without them. Each owner (any object: a module's, a mod's) holds one set of stats per
/// entity. A change is made in the area's next frame (GameArea.Defer), from any thread, and raises
/// <see cref="StatBuffsChanged"/> for the entity, whose stats are built again in that frame's Stats phase. No client buff
/// is shown: the stats themselves are.
/// </summary>
public static class AreaStatBuffs
{
    static readonly object Key = new();

    static Store<Dictionary<object, IReadOnlyDictionary<uint, int>>> Buffs(GameArea area) =>
        area.Store<Dictionary<object, IReadOnlyDictionary<uint, int>>>(Key);

    /// <summary>Changes the owner's stats on the entity (by stat id) in the area's next frame; left empty, they are removed.</summary>
    public static void ChangeStatBuff(this GameArea area, EntityId entity, object owner, Action<Dictionary<uint, int>> change) =>
        area.Defer(a =>
        {
            var buffs = Buffs(a).GetOrAdd(entity, _ => new(ReferenceEqualityComparer.Instance));
            var stats = buffs.TryGetValue(owner, out var had) ? new Dictionary<uint, int>(had) : [];
            change(stats);
            if (stats.Count == 0 && !buffs.Remove(owner)) return;
            if (stats.Count > 0) buffs[owner] = stats;
            a.Events.Raise(new StatBuffsChanged(entity));
        });

    /// <summary>The owner's stats on the entity, replacing what it had (in the area's next frame); empty removes them.</summary>
    public static void SetStatBuff(this GameArea area, EntityId entity, object owner, IReadOnlyDictionary<uint, int> stats)
    {
        var copy = new Dictionary<uint, int>(stats);
        area.ChangeStatBuff(entity, owner, s => { s.Clear(); foreach (var (id, value) in copy) s[id] = value; });
    }

    /// <summary>Takes the owner's stats off the entity (in the area's next frame).</summary>
    public static void RemoveStatBuff(this GameArea area, EntityId entity, object owner) =>
        area.ChangeStatBuff(entity, owner, s => s.Clear());

    /// <summary>The owner's stats on the entity as of the last frame, null when none.</summary>
    public static IReadOnlyDictionary<uint, int>? StatBuff(this GameArea area, EntityId entity, object owner) =>
        Buffs(area).TryGet(entity, out var buffs) ? buffs.GetValueOrDefault(owner) : null;

    /// <summary>Adds every stat buff on the entity into <paramref name="into"/>.</summary>
    public static void AddStatBuffs(this GameArea area, EntityId entity, IDictionary<uint, int> into)
    {
        if (!Buffs(area).TryGet(entity, out var buffs)) return;
        foreach (var stats in buffs.Values)
            foreach (var (id, value) in stats)
                into[id] = (int)Math.Clamp((into.TryGetValue(id, out int had) ? had : 0L) + value, int.MinValue, int.MaxValue);
    }
}

/// <summary>An entity's stat buffs changed (AreaStatBuffs).</summary>
public readonly record struct StatBuffsChanged(EntityId Entity);

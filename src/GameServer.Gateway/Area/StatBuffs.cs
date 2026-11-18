/// <summary>
/// Buffs on an area's entity that add stats while it is in that area, for as long as their owner keeps them (no
/// duration); a new area starts without them. Each owner (any object: a module's, a mod's) holds one set of stats per
/// entity, replaced or removed whole. A change raises <see cref="StatBuffsChanged"/> for the entity, and its stats are
/// built again in the area's next Stats phase. No client buff is shown: the stats themselves are.
/// </summary>
public static class AreaStatBuffs
{
    static readonly object Key = new();

    static Store<Dictionary<object, IReadOnlyDictionary<uint, int>>> Buffs(GameArea area) =>
        area.Store<Dictionary<object, IReadOnlyDictionary<uint, int>>>(Key);

    /// <summary>The owner's stats on the entity (by stat id), replacing what it had; an empty set removes it.</summary>
    public static void SetStatBuff(this GameArea area, EntityId entity, object owner, IReadOnlyDictionary<uint, int> stats)
    {
        if (stats.Count == 0) { area.RemoveStatBuff(entity, owner); return; }
        Buffs(area).GetOrAdd(entity, _ => new(ReferenceEqualityComparer.Instance))[owner] = new Dictionary<uint, int>(stats);
        area.Events.Raise(new StatBuffsChanged(entity));
    }

    /// <summary>Takes the owner's stats off the entity; false when it had none there.</summary>
    public static bool RemoveStatBuff(this GameArea area, EntityId entity, object owner)
    {
        if (!Buffs(area).TryGet(entity, out var buffs) || !buffs.Remove(owner)) return false;
        area.Events.Raise(new StatBuffsChanged(entity));
        return true;
    }

    /// <summary>The owner's stats on the entity, null when none.</summary>
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

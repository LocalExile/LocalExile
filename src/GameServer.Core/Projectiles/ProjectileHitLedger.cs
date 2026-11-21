namespace GameServer.Core.Projectiles;

/// <summary>Damage admission, independent of collision suppression. Keys include actor generations.</summary>
public sealed class ProjectileHitLedger
{
    readonly Dictionary<(long Group, int Phase, string Stage, ProjectileActorKey Target), long> hits = [];
    public bool CanHit(long group, int phase, string stage, ProjectileActorKey target, long at, long origin,
        ProjectileHitRule rule)
    {
        var key = (group, rule.Repeat == ProjectileRepeat.ContactInterval ? 0 : phase, stage, target);
        if (hits.TryGetValue(key, out long previous))
        {
            if (rule.Repeat == ProjectileRepeat.Once) return false;
            if (rule.Period <= 0) throw new ArgumentOutOfRangeException(nameof(rule));
            if (rule.Repeat is (ProjectileRepeat.Interval or ProjectileRepeat.ContactInterval) && at - previous < rule.Period) return false;
            if (rule.Repeat == ProjectileRepeat.Window && (at - origin) / rule.Period == (previous - origin) / rule.Period)
                return false;
        }
        return true;
    }
    public bool TryHit(long group, int phase, string stage, ProjectileActorKey target, long at, long origin,
        ProjectileHitRule rule, Func<bool>? commit = null)
    {
        if (!CanHit(group, phase, stage, target, at, origin, rule)) return false;
        if (commit is not null && !commit()) return false;
        hits[(group, rule.Repeat == ProjectileRepeat.ContactInterval ? 0 : phase, stage, target)] = at;
        return true;
    }
    internal long ContactFrom(long group, ProjectileActorKey target, long from, long period) =>
        hits.TryGetValue((group, 0, "direct", target), out long last) ? Math.Max(from, last + period) : from;

    public void Clear() => hits.Clear();
    public void RetainGroups(IReadOnlySet<long> active)
    {
        foreach (var key in hits.Keys.Where(key => !active.Contains(key.Group)).ToArray()) hits.Remove(key);
    }
}

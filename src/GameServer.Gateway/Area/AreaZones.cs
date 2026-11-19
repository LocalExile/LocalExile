/// <summary>
/// A radius around an entity (it follows its walk) or a fixed point, and the entities inside it: an aura, a ground, a
/// burning radius, a sight range. Who it reaches, and with what body size, is the owner's rule (<see cref="Reach"/>); the
/// area keeps the membership and tells the owner who entered and who left.
/// </summary>
public sealed class Zone
{
    /// <summary>The entity the zone is centred on; null for a zone at <see cref="At"/>.</summary>
    public EntityId? Follows { get; init; }
    /// <summary>The zone's centre when it follows no entity, or when that entity has no position this frame.</summary>
    public (int X, int Y) At { get; set; }
    /// <summary>The radius in cells, read on each membership update (it may change with stats).</summary>
    public required Func<int> Radius { get; init; }
    /// <summary>Whether the zone reaches an entity, and how far its body adds to the radius: null for one it never
    /// reaches (a friend of a hostile aura, a corpse).</summary>
    public required Func<EntityId, int?> Reach { get; init; }
    /// <summary>An entity came inside (in the area's frame, under the area's lock).</summary>
    public Action<EntityId>? Entered { get; init; }
    /// <summary>An entity left, died or was removed.</summary>
    public Action<EntityId>? Left { get; init; }

    internal readonly HashSet<EntityId> inside = [];
    internal (int X, int Y)? center;
    internal int radius = -1;
    internal object? source;

    /// <summary>The entities inside since the last membership update.</summary>
    public IReadOnlyCollection<EntityId> Inside => inside;
    public bool Contains(EntityId id) => inside.Contains(id);
}

/// <summary>
/// The area's zones. Their membership is updated in the movement phase from the walkers' positions: a zone is checked
/// again when a walker changed cell, the zone moved or its radius changed, never by scanning on a timer.
/// </summary>
public sealed class AreaZones
{
    readonly List<Zone> zones = [];
    bool added;

    public int Count => zones.Count;

    /// <summary>Adds a zone; its first members come at the next movement update.</summary>
    public Zone Add(Zone zone, object? source = null)
    {
        zone.source = source;
        zones.Add(zone);
        added = true;
        return zone;
    }

    /// <summary>Removes a zone without telling its owner who left: the owner ends what it gave them itself.</summary>
    public void Remove(Zone zone)
    {
        zones.Remove(zone);
        zone.inside.Clear();
    }

    internal void Drop(object source) => zones.RemoveAll(z => ReferenceEquals(z.source, source));

    /// <summary>
    /// Updates who is inside each zone from where every walker stands (<paramref name="positions"/>, the living ones),
    /// when <paramref name="moved"/> or the zone itself changed.
    /// </summary>
    public void Update(IReadOnlyDictionary<EntityId, (int X, int Y)> positions, bool moved)
    {
        bool any = added;
        added = false;
        foreach (var zone in zones.ToArray())
        {
            var center = zone.Follows is { } follows && positions.TryGetValue(follows, out var at) ? at : zone.At;
            int radius = zone.Radius();
            if (!moved && !any && center == zone.center && radius == zone.radius) continue;
            zone.center = center;
            zone.radius = radius;
            List<EntityId>? left = null;
            foreach (var id in zone.inside)
                if (!Reaches(zone, id, center, radius, positions)) (left ??= []).Add(id);
            if (left is not null)
                foreach (var id in left) { zone.inside.Remove(id); zone.Left?.Invoke(id); }
            foreach (var (id, _) in positions)
                if (!zone.inside.Contains(id) && Reaches(zone, id, center, radius, positions))
                {
                    zone.inside.Add(id);
                    zone.Entered?.Invoke(id);
                }
        }
    }

    static bool Reaches(Zone zone, EntityId id, (int X, int Y) center, int radius, IReadOnlyDictionary<EntityId, (int X, int Y)> positions)
    {
        if (!positions.TryGetValue(id, out var at) || zone.Reach(id) is not int body) return false;
        double dx = at.X - center.X, dy = at.Y - center.Y, reach = radius + body;
        return dx * dx + dy * dy <= reach * reach;
    }
}

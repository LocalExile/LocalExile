using System.Runtime.InteropServices;
using GameServer.Data.Terrain;

/// <summary>
/// Where the area's walkers stand, by grid cell: what is near a point is the entities of the few cells its radius
/// overlaps, not a pass over all of them. The movement phase sets every living walker each frame (Set, then
/// <see cref="EndPass"/>); an entity changes bucket only when it crosses into another cell. A walker's body (its
/// radius) is kept with it, and queries widen by the largest body so a big monster's edge is found from its centre.
/// Owned by the area's frame. The ground's items (InstanceGround) have one too: they never move.
/// </summary>
public sealed class AreaGrid
{
    // 16 world cells a side: a few buckets for a skill's radius (18-40), few entities per bucket in a crowd.
    const int Shift = 4;

    readonly Dictionary<long, List<EntityId>> buckets = [];
    readonly Dictionary<EntityId, Entry> entries = [];
    int pass;
    long clientTileArrival;

    struct Entry
    {
        public (int X, int Y) At;
        public int Body;
        public int Pass;
        public long ClientTileArrival;
    }

    public int Count => entries.Count;
    /// <summary>The largest body set so far: queries reach this much past their radius.</summary>
    public int MaxBody { get; private set; }

    static long BucketOf((int X, int Y) at) => (long)(at.X >> Shift) << 32 | (uint)(at.Y >> Shift);
    static (int Y, int X) ClientTileOf((int X, int Y) at) =>
        (at.Y / TilePlacement.CellsPerTile, at.X / TilePlacement.CellsPerTile);

    /// <summary>Where the entity stands; true when it is new or changed cell.</summary>
    public bool Set(EntityId id, (int X, int Y) at, int body = 0)
    {
        if (body > MaxBody) MaxBody = body;
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(entries, id, out bool exists);
        if (exists)
        {
            bool moved = entry.At != at;
            if (moved)
            {
                long oldBucket = BucketOf(entry.At), bucket = BucketOf(at);
                if (oldBucket != bucket)
                {
                    Unlist(id, oldBucket);
                    List(id, bucket);
                }
                if (ClientTileOf(entry.At) != ClientTileOf(at)) entry.ClientTileArrival = ++clientTileArrival;
                entry.At = at;
            }
            entry.Body = body;
            entry.Pass = pass;
            return moved;
        }
        List(id, BucketOf(at));
        entry = new Entry { At = at, Body = body, Pass = pass, ClientTileArrival = ++clientTileArrival };
        return true;
    }

    /// <summary>Ends a pass of <see cref="Set"/>: whoever was not set in it (died, left, removed) goes. True when one went.</summary>
    public bool EndPass()
    {
        List<EntityId>? gone = null;
        foreach (var (id, entry) in entries)
            if (entry.Pass != pass) (gone ??= []).Add(id);
        pass++;
        if (gone is null) return false;
        foreach (var id in gone) Remove(id);
        return true;
    }

    public bool Remove(EntityId id)
    {
        if (!entries.Remove(id, out var entry)) return false;
        Unlist(id, BucketOf(entry.At));
        return true;
    }

    /// <summary>The client's tile scan order: row, column, then order of entry to that tile. A position ahead of
    /// the last grid pass ranks after walkers that have already arrived there.</summary>
    public (int Y, int X, long Arrival, EntityId Id) ClientTileScanOrder(EntityId id, (int X, int Y) at)
    {
        var tile = ClientTileOf(at);
        long arrival = entries.TryGetValue(id, out var entry) && ClientTileOf(entry.At) == tile
            ? entry.ClientTileArrival : long.MaxValue;
        return (tile.Y, tile.X, arrival, id);
    }

    public bool TryGet(EntityId id, out (int X, int Y) at, out int body)
    {
        if (entries.TryGetValue(id, out var entry)) { (at, body) = (entry.At, entry.Body); return true; }
        (at, body) = (default, 0);
        return false;
    }

    /// <summary>Adds to <paramref name="into"/> every entity whose body reaches within <paramref name="radius"/> of
    /// <paramref name="center"/> (centre distance at most radius plus its body).</summary>
    public void Near((int X, int Y) center, int radius, List<EntityId> into)
    {
        int reach = radius + MaxBody;
        int x0 = (center.X - reach) >> Shift, x1 = (center.X + reach) >> Shift;
        int y0 = (center.Y - reach) >> Shift, y1 = (center.Y + reach) >> Shift;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!buckets.TryGetValue((long)x << 32 | (uint)y, out var list)) continue;
                foreach (var id in list)
                {
                    var entry = entries[id];
                    long dx = entry.At.X - center.X, dy = entry.At.Y - center.Y, r = radius + entry.Body;
                    if (dx * dx + dy * dy <= r * r) into.Add(id);
                }
            }
    }

    void List(EntityId id, long bucket)
    {
        if (!buckets.TryGetValue(bucket, out var list)) buckets[bucket] = list = [];
        list.Add(id);
    }

    void Unlist(EntityId id, long bucket)
    {
        var list = buckets[bucket];
        int at = list.IndexOf(id);
        list[at] = list[^1];
        list.RemoveAt(list.Count - 1);
        if (list.Count == 0) buckets.Remove(bucket);
    }
}

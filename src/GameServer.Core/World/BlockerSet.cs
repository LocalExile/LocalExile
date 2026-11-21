using System.Collections;

namespace GameServer.Core.World;

internal interface IBlockerQuery
{
    bool Blocks(int x, int y, int threshold, int radius);
}

/// <summary>A cell index for a simulation snapshot. Frame-local updates and exclusion views avoid rebuilding it per mover.</summary>
public sealed class BlockerSet : IReadOnlyList<ClientPathfinder.Blocker>, IBlockerQuery
{
    const int Bucket = 16;
    readonly List<ClientPathfinder.Blocker> all;
    // Intrusive singly-linked bucket chains parallel to `all`: avoids allocating a List<int> per occupied bucket.
    readonly List<int> next;
    readonly Dictionary<long, int> heads;
    int reach;
    int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

    public BlockerSet(int capacity)
    {
        all = new(capacity);
        next = new(capacity);
        heads = new(capacity);
    }

    public BlockerSet(IEnumerable<ClientPathfinder.Blocker> blockers)
    {
        int capacity = blockers is IReadOnlyCollection<ClientPathfinder.Blocker> rc ? rc.Count
            : blockers is ICollection<ClientPathfinder.Blocker> c ? c.Count : 0;
        all = capacity > 0 ? new(capacity) : [];
        next = capacity > 0 ? new(capacity) : [];
        heads = capacity > 0 ? new(capacity) : [];
        foreach (var blocker in blockers) Add(blocker);
    }

    public int Add(ClientPathfinder.Blocker blocker)
    {
        int index = all.Count;
        all.Add(blocker);
        next.Add(-1);
        Index(index, blocker);
        return index;
    }

    void Index(int index, ClientPathfinder.Blocker blocker)
    {
        TrackBounds(blocker);
        Link(Key(Floor(blocker.X), Floor(blocker.Y)), index);
    }

    void TrackBounds(ClientPathfinder.Blocker blocker)
    {
        int r = Math.Max(blocker.Size, blocker.CollisionRadius is double cr ? (int)Math.Ceiling(cr) : 0);
        if (r > reach) reach = r;
        if (blocker.X < minX) minX = blocker.X;
        if (blocker.X > maxX) maxX = blocker.X;
        if (blocker.Y < minY) minY = blocker.Y;
        if (blocker.Y > maxY) maxY = blocker.Y;
    }

    void Link(long key, int index)
    {
        next[index] = heads.TryGetValue(key, out int head) ? head : -1;
        heads[key] = index;
    }

    void Unlink(long key, int index)
    {
        if (!heads.TryGetValue(key, out int cur)) return;
        int prev = -1;
        while (cur >= 0)
        {
            if (cur == index)
            {
                int after = next[index];
                if (prev < 0)
                {
                    if (after < 0) heads.Remove(key);
                    else heads[key] = after;
                }
                else next[prev] = after;
                next[index] = -1;
                return;
            }
            prev = cur;
            cur = next[cur];
        }
    }

    public void Replace(int index, ClientPathfinder.Blocker blocker)
    {
        var previous = all[index];
        long oldKey = Key(Floor(previous.X), Floor(previous.Y)), newKey = Key(Floor(blocker.X), Floor(blocker.Y));
        all[index] = blocker;
        // An overestimate after a smaller or inward replacement only visits extra buckets; it never loses a blocker.
        TrackBounds(blocker);
        if (oldKey != newKey)
        {
            Unlink(oldKey, index);
            Link(newKey, index);
        }
    }

    /// <summary>Takes a blocker out of every query. Its slot stays, so the other indexes and the views hold; a list
    /// read finds a body off the map that blocks nothing.</summary>
    public void Remove(int index)
    {
        var previous = all[index];
        Unlink(Key(Floor(previous.X), Floor(previous.Y)), index);
        all[index] = Gone;
    }

    static readonly ClientPathfinder.Blocker Gone = new(int.MinValue / 2, int.MinValue / 2, -1);

    public int Count => all.Count;
    public ClientPathfinder.Blocker this[int index] => all[index];
    public IEnumerator<ClientPathfinder.Blocker> GetEnumerator() => all.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IReadOnlyList<ClientPathfinder.Blocker> Without(int first, int second = -1)
    {
        if ((uint)first >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(first));
        if (second == first) second = -1;
        if (second < -1 || second >= Count) throw new ArgumentOutOfRangeException(nameof(second));
        if (second >= 0 && second < first) (first, second) = (second, first);
        return new Excluding(this, first, second);
    }

    sealed class Excluding(BlockerSet owner, int first, int second) : IReadOnlyList<ClientPathfinder.Blocker>, IBlockerQuery
    {
        public int Count => owner.Count - (second < 0 ? 1 : 2);
        public ClientPathfinder.Blocker this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                if (index >= first) index++;
                if (second >= 0 && index >= second) index++;
                return owner[index];
            }
        }
        public IEnumerator<ClientPathfinder.Blocker> GetEnumerator()
        {
            for (int i = 0; i < owner.Count; i++) if (i != first && i != second) yield return owner[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Blocks(int x, int y, int threshold, int radius) => owner.Query(x, y, threshold, radius, first, second, false);
    }

    bool IBlockerQuery.Blocks(int x, int y, int threshold, int radius) => Query(x, y, threshold, radius, -1, -1, false);

    /// <summary>Square-footprint occupancy without native tile pruning or circular hulls (surround reservations).</summary>
    public bool OverlapsFootprint(int x, int y, int threshold) => Query(x, y, threshold, 0, -1, -1, true);

    bool Query(int x, int y, int threshold, int radius, int first, int second, bool footprintOnly)
    {
        if (heads.Count == 0) return false;
        int range = reach + threshold;
        if (x + range < minX || x - range > maxX || y + range < minY || y - range > maxY) return false;
        int tx0 = Math.Max(0, (x - radius) / 23), tx1 = (x + radius) / 23 + 1;
        int ty0 = Math.Max(0, (y - radius) / 23), ty1 = (y + radius) / 23 + 1;
        int bx0 = Floor(x - range), bx1 = Floor(x + range);
        int by0 = Floor(y - range), by1 = Floor(y + range);
        for (int bx = bx0; bx <= bx1; bx++)
            for (int by = by0; by <= by1; by++)
                if (heads.TryGetValue(Key(bx, by), out int index))
                    for (; index >= 0; index = next[index])
                    {
                        if (index == first || index == second) continue;
                        var b = all[index];
                        if (footprintOnly
                            ? Math.Max(Math.Abs(b.X - x), Math.Abs(b.Y - y)) - b.Size < threshold
                            : ClientPathfinder.BlockerBlocks(b, x, y, threshold, radius, tx0, tx1, ty0, ty1)) return true;
                    }
        return false;
    }

    static int Floor(int v) => v >> 4;
    static long Key(int bx, int by) => (long)bx << 32 | (uint)by;
}

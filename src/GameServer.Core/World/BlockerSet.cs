using System.Collections;
using System.Runtime.InteropServices;

namespace GameServer.Core.World;

internal interface IBlockerQuery
{
    bool Blocks(int x, int y, int threshold, int radius);
}

/// <summary>A cell index for a simulation snapshot. Frame-local updates and exclusion views avoid rebuilding it per mover.</summary>
public sealed class BlockerSet : IReadOnlyList<ClientPathfinder.Blocker>, IBlockerQuery
{
    const int Shift = 3;
    const int GridDim = 256;
    const int GridShift = 8;
    readonly List<ClientPathfinder.Blocker> all = [];
    // Flat 2D bucket grid covering [0..2047] x [0..2047] world cells (all standard PoE areas) for O(1) array-load
    // queries without Dictionary hashing, allocated on first in-range blocker so empty sets use no LOH buffer.
    // Out-of-range coordinates (e.g., negative test coords) fall back to overflow.
    List<int>?[]? grid;
    readonly List<int> occupiedSlots = [];
    readonly Dictionary<long, List<int>> overflow = [];
    readonly Stack<List<int>> bucketPool = [];
    int reach;
    int activeCount;
    int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

    public BlockerSet() { }

    public BlockerSet(IEnumerable<ClientPathfinder.Blocker> blockers)
    {
        foreach (var blocker in blockers) Add(blocker);
    }

    public void Reset(IReadOnlyList<ClientPathfinder.Blocker> blockers)
    {
        all.Clear();
        if (grid is not null)
        {
            var slots = CollectionsMarshal.AsSpan(occupiedSlots);
            for (int i = 0; i < slots.Length; i++)
            {
                int slot = slots[i];
                var list = grid[slot];
                if (list is not null)
                {
                    list.Clear();
                    bucketPool.Push(list);
                    grid[slot] = null;
                }
            }
            occupiedSlots.Clear();
        }
        if (overflow.Count > 0)
        {
            foreach (var list in overflow.Values)
            {
                list.Clear();
                bucketPool.Push(list);
            }
            overflow.Clear();
        }
        reach = 0;
        activeCount = 0;
        minX = int.MaxValue; maxX = int.MinValue; minY = int.MaxValue; maxY = int.MinValue;
        for (int i = 0; i < blockers.Count; i++) Add(blockers[i]);
    }

    public int Add(ClientPathfinder.Blocker blocker)
    {
        int index = all.Count;
        all.Add(blocker);
        activeCount++;
        Index(index, blocker);
        return index;
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

    void Index(int index, ClientPathfinder.Blocker blocker)
    {
        TrackBounds(blocker);
        int bx = Floor(blocker.X), by = Floor(blocker.Y);
        if ((uint)bx < GridDim && (uint)by < GridDim)
        {
            grid ??= new List<int>?[GridDim * GridDim];
            int slot = (bx << GridShift) | by;
            var list = grid[slot];
            if (list is null)
            {
                grid[slot] = list = bucketPool.Count > 0 ? bucketPool.Pop() : [];
                occupiedSlots.Add(slot);
            }
            list.Add(index);
            return;
        }
        long key = Key(bx, by);
        if (!overflow.TryGetValue(key, out var olist))
            overflow[key] = olist = bucketPool.Count > 0 ? bucketPool.Pop() : [];
        olist.Add(index);
    }

    public void Replace(int index, ClientPathfinder.Blocker blocker)
    {
        var previous = all[index];
        int oldBx = Floor(previous.X), oldBy = Floor(previous.Y);
        int newBx = Floor(blocker.X), newBy = Floor(blocker.Y);
        all[index] = blocker;
        // An overestimate after a smaller or inward replacement only visits extra buckets; it never loses a blocker.
        TrackBounds(blocker);
        if (oldBx != newBx || oldBy != newBy)
        {
            RemoveFromBucket(index, oldBx, oldBy);
            Index(index, blocker);
        }
    }

    void RemoveFromBucket(int index, int bx, int by)
    {
        if ((uint)bx < GridDim && (uint)by < GridDim)
        {
            if (grid is null) return;
            int slot = (bx << GridShift) | by;
            var list = grid[slot];
            if (list is not null && list.Remove(index) && list.Count == 0)
            {
                grid[slot] = null;
                bucketPool.Push(list);
            }
            return;
        }
        long key = Key(bx, by);
        if (overflow.TryGetValue(key, out var olist) && olist.Remove(index) && olist.Count == 0)
        {
            overflow.Remove(key);
            bucketPool.Push(olist);
        }
    }

    /// <summary>Takes a blocker out of every query. Its slot stays, so the other indexes and the views hold; a list
    /// read finds a body off the map that blocks nothing.</summary>
    public void Remove(int index)
    {
        var previous = all[index];
        if (previous == Gone) return;
        RemoveFromBucket(index, Floor(previous.X), Floor(previous.Y));
        all[index] = Gone;
        activeCount--;
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
        if (activeCount == 0) return false;
        int range = reach + threshold;
        if (x + range < minX || x - range > maxX || y + range < minY || y - range > maxY) return false;
        int tx0 = Math.Max(0, (x - radius) / 23), tx1 = (x + radius) / 23 + 1;
        int ty0 = Math.Max(0, (y - radius) / 23), ty1 = (y + radius) / 23 + 1;
        int bx0 = Floor(x - range), bx1 = Floor(x + range);
        int by0 = Floor(y - range), by1 = Floor(y + range);
        var allSpan = CollectionsMarshal.AsSpan(all);
        var g = grid;
        if (g is not null && (uint)bx0 < GridDim && (uint)bx1 < GridDim && (uint)by0 < GridDim && (uint)by1 < GridDim)
        {
            for (int bx = bx0; bx <= bx1; bx++)
            {
                int row = bx << GridShift;
                for (int by = by0; by <= by1; by++)
                {
                    var list = g[row | by];
                    if (list is null) continue;
                    var indices = CollectionsMarshal.AsSpan(list);
                    for (int i = 0; i < indices.Length; i++)
                    {
                        int index = indices[i];
                        if (index == first || index == second) continue;
                        ref readonly var b = ref allSpan[index];
                        if (footprintOnly)
                        {
                            int dx = b.X - x; if (dx < 0) dx = -dx;
                            int dy = b.Y - y; if (dy < 0) dy = -dy;
                            if ((dx > dy ? dx : dy) - b.Size < threshold) return true;
                        }
                        else if (ClientPathfinder.BlockerBlocks(b, x, y, threshold, radius, tx0, tx1, ty0, ty1)) return true;
                    }
                }
            }
            return false;
        }
        bool hasOverflow = overflow.Count > 0;
        for (int bx = bx0; bx <= bx1; bx++)
            for (int by = by0; by <= by1; by++)
            {
                List<int>? list = (uint)bx < GridDim && (uint)by < GridDim
                    ? g?[(bx << GridShift) | by]
                    : (hasOverflow && overflow.TryGetValue(Key(bx, by), out var olist) ? olist : null);
                if (list is null) continue;
                var indices = CollectionsMarshal.AsSpan(list);
                for (int i = 0; i < indices.Length; i++)
                {
                    int index = indices[i];
                    if (index == first || index == second) continue;
                    ref readonly var b = ref allSpan[index];
                    if (footprintOnly
                        ? Math.Max(Math.Abs(b.X - x), Math.Abs(b.Y - y)) - b.Size < threshold
                        : ClientPathfinder.BlockerBlocks(b, x, y, threshold, radius, tx0, tx1, ty0, ty1)) return true;
                }
            }
        return false;
    }

    static int Floor(int v) => v >> Shift;
    static long Key(int bx, int by) => (long)bx << 32 | (uint)by;
}

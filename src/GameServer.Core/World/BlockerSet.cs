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
    readonly List<ClientPathfinder.Blocker> all = [];
    readonly Dictionary<long, List<int>> buckets = [];
    int reach;

    public BlockerSet(IEnumerable<ClientPathfinder.Blocker> blockers)
    {
        foreach (var blocker in blockers) Add(blocker);
    }

    public int Add(ClientPathfinder.Blocker blocker)
    {
        int index = all.Count;
        all.Add(blocker);
        Index(index, blocker);
        return index;
    }

    void Index(int index, ClientPathfinder.Blocker blocker)
    {
        reach = Math.Max(reach, Math.Max(blocker.Size,
            blocker.CollisionRadius is double r ? (int)Math.Ceiling(r) : 0));
        long key = Key(Floor(blocker.X), Floor(blocker.Y));
        if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
        list.Add(index);
    }

    public void Replace(int index, ClientPathfinder.Blocker blocker)
    {
        var previous = all[index];
        long oldKey = Key(Floor(previous.X), Floor(previous.Y)), newKey = Key(Floor(blocker.X), Floor(blocker.Y));
        all[index] = blocker;
        if (oldKey != newKey)
        {
            var old = buckets[oldKey]; old.Remove(index);
            if (old.Count == 0) buckets.Remove(oldKey);
            Index(index, blocker);
        }
        // An overestimate after a smaller replacement only visits extra buckets; it never loses a blocker.
        reach = Math.Max(reach, Math.Max(blocker.Size,
            blocker.CollisionRadius is double r ? (int)Math.Ceiling(r) : 0));
    }

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
        int range = reach + threshold;
        int tx0 = Math.Max(0, (x - radius) / 23), tx1 = (x + radius) / 23 + 1;
        int ty0 = Math.Max(0, (y - radius) / 23), ty1 = (y + radius) / 23 + 1;
        for (int bx = Floor(x - range); bx <= Floor(x + range); bx++)
            for (int by = Floor(y - range); by <= Floor(y + range); by++)
                if (buckets.TryGetValue(Key(bx, by), out var list))
                    foreach (int index in list)
                    {
                        if (index == first || index == second) continue;
                        var b = all[index];
                        if (footprintOnly
                            ? Math.Max(Math.Abs(b.X - x), Math.Abs(b.Y - y)) - b.Size < threshold
                            : ClientPathfinder.BlockerBlocks(b, x, y, threshold, radius, tx0, tx1, ty0, ty1)) return true;
                    }
        return false;
    }

    static int Floor(int v) => (int)Math.Floor(v / (double)Bucket);
    static long Key(int bx, int by) => (long)bx << 32 | (uint)by;
}

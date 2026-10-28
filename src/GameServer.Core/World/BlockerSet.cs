using System.Collections;

namespace GameServer.Core.World;

/// <summary>
/// Blockers bucketed by cell, so <see cref="ClientPathfinder.ObjectBlocks"/> reads only those that can reach the queried
/// cell instead of every blocker. A blocker can block only within its reach (size, or collision radius) plus the
/// threshold, so the answer is the same as scanning the whole list. Living collision and monster paths query it per cell.
/// </summary>
public sealed class BlockerSet : IReadOnlyList<ClientPathfinder.Blocker>
{
    const int Bucket = 16;
    readonly List<ClientPathfinder.Blocker> _all;
    readonly Dictionary<long, List<ClientPathfinder.Blocker>> _buckets = [];
    readonly int _reach;

    public BlockerSet(IEnumerable<ClientPathfinder.Blocker> blockers)
    {
        _all = [.. blockers];
        foreach (var b in _all)
        {
            _reach = Math.Max(_reach, Math.Max(b.Size, b.CollisionRadius is double r ? (int)Math.Ceiling(r) : 0));
            long key = Key(Floor(b.X), Floor(b.Y));
            if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = [];
            list.Add(b);
        }
    }

    public int Count => _all.Count;
    public ClientPathfinder.Blocker this[int index] => _all[index];
    public IEnumerator<ClientPathfinder.Blocker> GetEnumerator() => _all.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The blockers whose Chebyshev distance to (x, y) may be below <paramref name="threshold"/> + their reach.</summary>
    internal IEnumerable<ClientPathfinder.Blocker> Near(int x, int y, int threshold)
    {
        int range = _reach + threshold;
        for (int bx = Floor(x - range); bx <= Floor(x + range); bx++)
            for (int by = Floor(y - range); by <= Floor(y + range); by++)
                if (_buckets.TryGetValue(Key(bx, by), out var list))
                    foreach (var b in list) yield return b;
    }

    static int Floor(int v) => (int)Math.Floor(v / (double)Bucket);
    static long Key(int bx, int by) => (long)bx << 32 | (uint)by;
}

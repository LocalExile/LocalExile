

/// <summary>
/// The cells a quest's force wall closes on the server's walk map (Agents/docs/protocol/12-quests.md §6). The client
/// blocks the wall itself, but the server's walk estimate knew nothing of it: it walked on through the wall, and the next
/// echo snapped the player to the far side. The room puts The Twilight Strand's wall in the rock beside the way; it
/// blocks where its line crosses the neck from Hillock's beach into the gate corridor (live 2026-10-21: the player stood
/// stuck 1.3 cells from the line). So the server closes the walkable stretches of the wall's line (its angle as given,
/// both directions, up to <see cref="Reach"/> cells), each widened to <see cref="HalfWidth"/> cells either side, wider
/// than the client's wall so the server's position never lands inside it, and kept only when closing it cuts the
/// guarded exits off from <c>from</c> (the boss's side). Stretches that cut nothing (open beach) stay open.
/// </summary>
static class QuestBarriers
{
    /// <summary>How far (cells) from the wall's centre its line is followed.</summary>
    public const int Reach = 60;

    /// <summary>How far (cells) either side of the line the closed band reaches.</summary>
    public const double HalfWidth = 2.5;

    public static HashSet<(int X, int Y)> Cells(Func<int, int, bool> walkable, int w, int h, (int X, int Y) centre, float angle,
        (int X, int Y) from, IReadOnlyList<(int X, int Y)> guarded)
    {
        var result = new HashSet<(int X, int Y)>();
        if (guarded.Count == 0 || !walkable(from.X, from.Y)) return result;
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && walkable(x, y);
        // Exits the boss's side cannot reach anyway are not guarded by this wall.
        if (Separates(Inside, w, h, [], from, guarded)) return result;
        foreach (int dir in new[] { 1, -1 })
            {
                double dx = Math.Cos(angle) * dir, dy = Math.Sin(angle) * dir;
                var run = new HashSet<(int X, int Y)>();
                for (double t = 0; t <= Reach; t += 0.5)
                {
                    double x = centre.X + dx * t, y = centre.Y + dy * t;
                    if (Inside((int)Math.Round(x), (int)Math.Round(y)))
                    {
                        int r = (int)Math.Ceiling(HalfWidth);
                        for (int cy = (int)Math.Floor(y) - r; cy <= (int)Math.Ceiling(y) + r; cy++)
                            for (int cx = (int)Math.Floor(x) - r; cx <= (int)Math.Ceiling(x) + r; cx++)
                                if ((cx - x) * (cx - x) + (cy - y) * (cy - y) <= HalfWidth * HalfWidth && Inside(cx, cy)) run.Add((cx, cy));
                        continue;
                    }
                    if (run.Count > 0 && Separates(Inside, w, h, run, from, guarded)) result.UnionWith(run);
                    run = [];
                }
                if (run.Count > 0 && Separates(Inside, w, h, run, from, guarded)) result.UnionWith(run);
            }
        return result;
    }

    /// <summary>Whether closing these cells leaves no 4-neighbour walk from <paramref name="from"/> to any guarded cell
    /// (which could be reached before).</summary>
    static bool Separates(Func<int, int, bool> inside, int w, int h, HashSet<(int X, int Y)> closed, (int X, int Y) from,
        IReadOnlyList<(int X, int Y)> guarded)
    {
        if (closed.Contains(from)) return false;
        var seen = new bool[w * h];
        var queue = new Queue<(int X, int Y)>();
        seen[from.Y * w + from.X] = true;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (!inside(nx, ny) || seen[ny * w + nx] || closed.Contains((nx, ny))) continue;
                seen[ny * w + nx] = true;
                queue.Enqueue((nx, ny));
            }
        }
        return guarded.All(g => g.X >= 0 && g.Y >= 0 && g.X < w && g.Y < h && !seen[g.Y * w + g.X]);
    }
}

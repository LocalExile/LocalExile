

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

    // force_wall.ao's Hull (proj_tri) in local cells before Positioned uniform scale (DoodadHull / 0x141d8ad70):
    // x in [-19.52, +19.24], y in [-3.96, +5.16].
    const double ForceWallMinX = -19.52, ForceWallMaxX = 19.24, ForceWallMinY = -3.96, ForceWallMaxY = 5.16;

    /// <summary>Walkable cells inside a scaled <c>force_wall.ao</c> Hull (matching the client's 0x141d8ad70 blockage),
    /// kept when closing them separates <paramref name="from"/> from <paramref name="guarded"/>; falls back to
    /// <see cref="Cells"/> otherwise.</summary>
    public static HashSet<(int X, int Y)> ForceWallCells(Func<int, int, bool> walkable, int w, int h, (int X, int Y) centre,
        float angle, float scale, (int X, int Y) from, IReadOnlyList<(int X, int Y)> guarded)
    {
        if (guarded.Count == 0 || !walkable(from.X, from.Y)) return [];
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && walkable(x, y);
        if (Separates(Inside, w, h, [], from, guarded)) return [];
        double sc = scale > 0 ? scale : 1;
        double minX = ForceWallMinX * sc, maxX = ForceWallMaxX * sc, minY = ForceWallMinY * sc, maxY = ForceWallMaxY * sc;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        int reach = (int)Math.Ceiling(Math.Sqrt(Math.Max(minX * minX, maxX * maxX) + Math.Max(minY * minY, maxY * maxY))) + 1;
        var hull = new HashSet<(int X, int Y)>();
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                double lx = dx * cos + dy * sin, ly = -dx * sin + dy * cos;
                if (lx >= minX && lx <= maxX && ly >= minY && ly <= maxY && Inside(centre.X + dx, centre.Y + dy))
                    hull.Add((centre.X + dx, centre.Y + dy));
            }
        if (hull.Count > 0 && Separates(Inside, w, h, hull, from, guarded)) return hull;
        return Cells(walkable, w, h, centre, angle, from, guarded);
    }

    public static HashSet<(int X, int Y)> Cells(Func<int, int, bool> walkable, int w, int h, (int X, int Y) centre, float angle,
        (int X, int Y) from, IReadOnlyList<(int X, int Y)> guarded)
    {
        var result = new HashSet<(int X, int Y)>();
        if (guarded.Count == 0 || !walkable(from.X, from.Y)) return result;
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && walkable(x, y);
        // Exits the boss's side cannot reach anyway are not guarded by this wall.
        if (Separates(Inside, w, h, [], from, guarded)) return result;
        // Sweep one continuous line across centre (-Reach..+Reach) so a wall whose centre lies on walkable terrain
        // (Tutorial_Blocker_1) keeps both halves of the crossing in a single stretch.
        double dx = Math.Cos(angle), dy = Math.Sin(angle);
        var run = new HashSet<(int X, int Y)>();
        int r = (int)Math.Ceiling(HalfWidth);
        for (double t = -Reach; t <= Reach; t += 0.5)
        {
            double x = centre.X + dx * t, y = centre.Y + dy * t;
            if (Inside((int)Math.Round(x), (int)Math.Round(y)))
            {
                for (int cy = (int)Math.Floor(y) - r; cy <= (int)Math.Ceiling(y) + r; cy++)
                    for (int cx = (int)Math.Floor(x) - r; cx <= (int)Math.Ceiling(x) + r; cx++)
                        if ((cx - x) * (cx - x) + (cy - y) * (cy - y) <= HalfWidth * HalfWidth && Inside(cx, cy)) run.Add((cx, cy));
                continue;
            }
            if (run.Count > 0 && Separates(Inside, w, h, run, from, guarded)) result.UnionWith(run);
            run = [];
        }
        if (run.Count > 0 && Separates(Inside, w, h, run, from, guarded)) result.UnionWith(run);
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

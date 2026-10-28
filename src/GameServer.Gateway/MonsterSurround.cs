/// <summary>
/// Where chasing monsters stand around the player. Each monster used to aim at the cell its stop range away on the line
/// from the player toward itself, so a pack coming from one side aimed at nearly one cell: the bodies queued behind each
/// other in a wedge and only the front ones reached the player. Here the nearest monsters choose first and each takes the
/// free cell on the innermost ring around the player closest to its own bearing (or to its previous slot, so slots stay
/// put while the player stands); a full ring sends the rest to the next ring out, where they wait for a gap.
/// </summary>
static class MonsterSurround
{
    /// <summary>A monster asking for a cell: its id, its cell, its Positioned size (object_size − 1) and its last slot.</summary>
    public readonly record struct Asker(uint Id, int X, int Y, int Size, (int X, int Y)? Last);

    /// <summary>How many rings out a monster may be placed before it simply aims at the player.</summary>
    const int Rings = 6;

    /// <summary>
    /// The innermost ring's radius for a body of this size: the closest its centre can stand to the player's (size 1)
    /// without the bodies blocking each other (ClientPathfinder.ObjectBlocks: Chebyshev distance − size ≥ mover size + 1).
    /// </summary>
    public static int ContactRadius(int size, int playerSize = LivingCollision.PlayerSize) => size + playerSize + 2;

    /// <param name="askers">The monsters to place, any order (the nearest are placed first).</param>
    /// <param name="occupied">Bodies that are not asking (they stand, swing or are not chasing): no slot overlaps them.</param>
    /// <param name="walkable">Whether a body of the given size may stand on a cell.</param>
    public static Dictionary<uint, (int X, int Y)> Assign(IEnumerable<Asker> askers, (int X, int Y) player,
        IEnumerable<(int X, int Y, int Size)> occupied, Func<int, int, int, bool> walkable)
    {
        var taken = new List<(int X, int Y, int Size)>(occupied);
        var result = new Dictionary<uint, (int X, int Y)>();
        foreach (var a in askers.OrderBy(a => Distance2(a.X - player.X, a.Y - player.Y)).ThenBy(a => a.Id))
        {
            bool Free((int X, int Y) c) => walkable(c.X, c.Y, a.Size)
                && taken.All(t => Math.Max(Math.Abs(t.X - c.X), Math.Abs(t.Y - c.Y)) >= t.Size + a.Size + 2);
            // Keep the last slot while it is still free and still on the innermost ring around the player; a waiting slot
            // farther out is chosen again every time, so it moves in when a gap opens.
            (int X, int Y)? slot = a.Last is { } last && OnRing(last, player, a.Size, maxRing: 0) && Free(last) ? last : null;
            if (slot is null)
            {
                // A last slot still on its ring but taken: look beside it. Once the player has moved, from the monster.
                double bearing = a.Last is { } l && (l.X, l.Y) != player && OnRing(l, player, a.Size, maxRing: 0)
                    ? Math.Atan2(l.Y - player.Y, l.X - player.X)
                    : Math.Atan2(a.Y - player.Y, a.X - player.X);
                slot = Find(player, a.Size, bearing, Free);
            }
            if (slot is not { } s) continue;
            result[a.Id] = s;
            taken.Add((s.X, s.Y, a.Size));
        }
        return result;
    }

    static (int X, int Y)? Find((int X, int Y) player, int size, double bearing, Func<(int X, int Y), bool> free)
    {
        int spacing = 2 * size + 2;
        for (int ring = 0; ring < Rings; ring++)
        {
            double radius = ContactRadius(size) + ring * spacing;
            // One cell along the ring per step, both sides of the bearing alternately, nearest first: the first free cell
            // packs the bodies as tightly as they may stand.
            double step = 1.0 / radius;
            int steps = (int)Math.Ceiling(Math.PI / step);
            for (int k = 0; k <= steps; k++)
                foreach (int sign in k == 0 ? [1] : new[] { 1, -1 })
                {
                    double angle = bearing + sign * k * step;
                    var cell = ((int)Math.Round(player.X + radius * Math.Cos(angle)), (int)Math.Round(player.Y + radius * Math.Sin(angle)));
                    if (free(cell)) return cell;
                }
        }
        return null;
    }

    static bool OnRing((int X, int Y) cell, (int X, int Y) player, int size, int maxRing)
    {
        double d = Math.Sqrt(Distance2(cell.X - player.X, cell.Y - player.Y)), r0 = ContactRadius(size);
        int spacing = 2 * size + 2;
        if (d < r0 - 1) return false;
        double ring = (d - r0) / spacing;
        return Math.Round(ring) <= maxRing && Math.Abs(ring - Math.Round(ring)) * spacing <= 1.5;
    }

    static long Distance2(int dx, int dy) => (long)dx * dx + (long)dy * dy;
}

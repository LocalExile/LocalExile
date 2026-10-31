using GameServer.Core.World;


/// <summary>Finds a nearby safe cell when static furniture or terrain encloses the player.</summary>
static class CollisionEscape
{
    public static bool Clear((int X, int Y) at, int width, int height, Func<int, int, bool>? walkable,
        IReadOnlyList<ClientPathfinder.Blocker> blockers, int moverSize = 1) =>
        at.X >= 0 && at.Y >= 0 && at.X < width && at.Y < height && walkable?.Invoke(at.X, at.Y) != false
        && !ClientPathfinder.ObjectBlocks(blockers, at.X, at.Y, moverSize + 1, moverSize + 4);

    public static (int X, int Y)? Find((int X, int Y) from, int width, int height,
        Func<int, int, bool>? walkable, IReadOnlyList<ClientPathfinder.Blocker> blockers, int maxRadius = 128, int moverSize = 1)
    {
        if (width <= 0 || height <= 0 || Clear(from, width, height, walkable, blockers, moverSize)) return null;
        (int X, int Y)? best = null;
        long distance = long.MaxValue;
        void Try(int dx, int dy)
        {
            long d = (long)dx * dx + (long)dy * dy;
            if (d >= distance) return;
            var at = (from.X + dx, from.Y + dy);
            if (!Clear(at, width, height, walkable, blockers, moverSize)) return;
            best = at;
            distance = d;
        }
        for (int radius = 1; radius <= maxRadius && (long)radius * radius <= distance; radius++)
            for (int offset = -radius; offset <= radius; offset++)
            {
                Try(-radius, offset); Try(radius, offset);
                Try(offset, -radius); Try(offset, radius);
            }
        return best;
    }
}

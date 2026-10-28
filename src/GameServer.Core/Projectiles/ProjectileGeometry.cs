namespace GameServer.Core.Projectiles;

public static class ProjectileGeometry
{
    /// <summary>Entry time into a swept relative circle, including overlap and tangency.</summary>
    public static double? Contact(ProjectileVector from, ProjectileVector to, double radius)
    {
        double c = from.Dot(from) - radius * radius;
        if (c <= 0) return 0;
        var delta = to - from;
        double a = delta.Dot(delta), b = from.Dot(delta);
        if (a <= 0 || b >= 0) return null;
        double discriminant = b * b - a * c;
        if (discriminant < 0) return null;
        double t = (-b - Math.Sqrt(discriminant)) / a;
        return t is >= 0 and <= 1 ? t : null;
    }

    /// <summary>Supercover grid traversal. Cells are centered on integer coordinates.</summary>
    public static TerrainContact? TraceGrid(ProjectileVector from, ProjectileVector to, Func<int, int, bool> blocked)
    {
        int x = (int)Math.Floor(from.X + .5), y = (int)Math.Floor(from.Y + .5);
        var delta = to - from;
        int sx = Math.Sign(delta.X), sy = Math.Sign(delta.Y);
        if (blocked(x, y)) return new(0, delta.Unit * -1);
        double tx = sx == 0 ? double.PositiveInfinity : (x + .5 * sx - from.X) / delta.X;
        double ty = sy == 0 ? double.PositiveInfinity : (y + .5 * sy - from.Y) / delta.Y;
        double stepX = sx == 0 ? double.PositiveInfinity : 1 / Math.Abs(delta.X);
        double stepY = sy == 0 ? double.PositiveInfinity : 1 / Math.Abs(delta.Y);
        while (Math.Min(tx, ty) <= 1)
        {
            bool cx = tx <= ty + 1e-12, cy = ty <= tx + 1e-12;
            if ((cx && blocked(x + sx, y)) || (cy && blocked(x, y + sy))
                || (cx && cy && blocked(x + sx, y + sy)))
                return new(Math.Max(0, Math.Min(tx, ty)), new ProjectileVector(cx ? -sx : 0, cy ? -sy : 0).Unit);
            if (cx) { x += sx; tx += stepX; }
            if (cy) { y += sy; ty += stepY; }
        }
        return null;
    }
}

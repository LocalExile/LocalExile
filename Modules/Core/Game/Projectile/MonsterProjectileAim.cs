using GameServer.Core.Projectiles;

static partial class Game
{
    // MonsterMortarAttack's target setup (3.29.3b, 141b69ba0). Minimum range is measured from the
    // actor in cells; scatter follows it. The animation-speed draw is consumed even with zero variation.
    internal static ProjectileVector MonsterMortarTarget(ProjectileVector actor, ProjectileVector origin,
        ProjectileVector aim, int minimumRange, int spread, ushort sequence, uint owner, int index)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, (uint)(index + 2), owner, 0, 0, 0, 0, 0]);
        _ = random.Range(0, 2);
        int x = (int)aim.X, y = (int)aim.Y, ax = (int)actor.X, ay = (int)actor.Y;
        int Round(float value) => (int)MathF.Floor(value + .5f);
        int dx = x - ax, dy = y - ay;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < minimumRange)
        {
            if (length == 0)
            {
                float angle = random.Range(0, 6.2831855f);
                x = ax + Round(MathF.Sin(angle) * minimumRange);
                y = ay - Round(MathF.Cos(angle) * minimumRange);
            }
            else
            {
                float scale = minimumRange / length;
                x = ax + Round(dx * scale); y = ay + Round(dy * scale);
            }
        }
        if (spread <= 0) return new(x, y);
        int ox = (int)origin.X, oy = (int)origin.Y;
        dx = x - ox; dy = y - oy;
        if (dx * dx + dy * dy >= spread * spread)
        {
            float angle = random.Range(0, 6.2831855f);
            float radius = MathF.Sqrt(random.Range(0, spread * spread));
            return new(x + (int)(MathF.Sin(angle) * radius), y - (int)(MathF.Cos(angle) * radius));
        }
        // The close-target branch samples the two chords from the launch cell, rather than scattering behind it.
        if (dx == 0 && dy == 0) dx = 1;
        float factor = spread / MathF.Sqrt(dx * dx + dy * dy);
        int bx = ox + Round(dx * factor), by = oy + Round(dy * factor);
        if (bx == x && by == y)
        {
            if (Math.Abs(dx) > Math.Abs(dy)) bx += Math.Sign(dx);
            else by += Math.Sign(dy);
        }
        int ex = bx - x, ey = by - y;
        float distance = MathF.Sqrt(ex * ex + ey * ey), half = distance / 2;
        float height = MathF.Sqrt(Math.Max(0, spread * spread - half * half));
        int cx = (int)(ex * half / distance + x), cy = (int)(ey * half / distance + y);
        float px = ey * height / distance, py = ex * height / distance;
        int ux = (int)(cx + px) - ox, uy = (int)(cy - py) - oy;
        int vx = (int)(cx - px) - ox, vy = (int)(cy + py) - oy;
        float u = random.Range(0, 1), v = random.Range(0, 1);
        return new(ox + Round(ux * u) + Round(vx * v), oy + Round(uy * u) + Round(vy * v));
    }
}

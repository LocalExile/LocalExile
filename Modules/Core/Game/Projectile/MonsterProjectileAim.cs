using GameServer.Core.Projectiles;

public static partial class Game
{
    // MonsterMortarAttack's target setup (3.29.3c, 141b6c630). Minimum range is measured from the
    // actor in cells; scatter follows it. The animation-speed draw is consumed even with zero variation.
    public static ProjectileVector MonsterMortarTarget(ProjectileVector actor, ProjectileVector origin,
        ProjectileVector aim, int minimumRange, int spread, ushort sequence, uint owner, int index) =>
        MonsterMortarTarget(actor, origin, aim, minimumRange, spread, sequence, owner, index, 0, out _);

    // A water vortex's end (0x141c6cc80): on the L1 diamond of radius d around the caster's cell, in the aim's direction
    // up to integer rounding (u : d − u = |dx| : |dy|), the caster's cell nudged one cell in x when it is the aim's.
    public static ProjectileVector WaterVortexTarget(ProjectileVector actor, ProjectileVector aim, int d)
    {
        int ox = (int)actor.X, oy = (int)actor.Y, dx = (int)aim.X - ox, dy = (int)aim.Y - oy;
        if (dx == 0 && dy == 0) dx = -1;
        int l1 = Math.Abs(dx) + Math.Abs(dy);
        int u = (l1 / 2 + Math.Abs(dx) * d) / l1;
        return new(ox + Math.Sign(dx) * u, oy + (dy >= 0 ? d - u : -(d - u)));
    }

    // An ethereal knife's end (mover 0x1419f46c0): its fanned target moved by two draws of 0..100 (the first in y), each
    // /5 − 10 truncated, then stretched from its launch cell to its range, truncated per axis.
    public static ProjectileVector ShadowKnifeEnd(ProjectileVector target, ProjectileVector launch, ushort sequence, uint derived,
        uint repeatsLeft, float range)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, derived, repeatsLeft]);
        float a = random.Range(0, 100), b = random.Range(0, 100);
        int px = (int)launch.X, py = (int)launch.Y;
        int vx = (int)target.X + (int)(b / 5 - 10) - px, vy = (int)target.Y + (int)(a / 5 - 10) - py;
        float length = MathF.Sqrt(vx * vx + vy * vy);
        if (length >= .01f) (vx, vy) = ((int)(vx * (range / length)), (int)(vy * (range / length)));
        return new(px + vx, py + vy);
    }

    // The spell form's mover (0x141a10d50) runs the same steps, with mortar_projectile_distance_override between the
    // minimum range and the scatter: the landing rescaled to that distance from the launch (both forms, 0x141b6a017).
    // The first draw is the speed variation the spell form's flight time takes.
    public static ProjectileVector MonsterMortarTarget(ProjectileVector actor, ProjectileVector origin,
        ProjectileVector aim, int minimumRange, int spread, ushort sequence, uint owner, int index, int distanceOverride,
        out float speedDraw)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, (uint)(index + 2), owner, 0, 0, 0, 0, 0]);
        speedDraw = random.Range(0, 2);
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
        int ox = (int)origin.X, oy = (int)origin.Y;
        if (distanceOverride > 0 && (x != ox || y != oy))
        {
            float scale = distanceOverride / MathF.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
            x = ox + Round((x - ox) * scale); y = oy + Round((y - oy) * scale);
        }
        if (spread <= 0) return new(x, y);
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

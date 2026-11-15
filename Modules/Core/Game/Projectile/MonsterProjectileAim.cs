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

    // A seeded mover's end: an ethereal knife's (0x1419f46c0, key [sequence, derived, repeats left]), a fire spray's
    // (0x141c6b730, key [sequence, derived, repeats left, earlier releases], which first draws its speed factor 0.6..1.4),
    // an incinerate flame's (0x141b85a60, 0x141b86ad0, 0x141b81b90; the knife's key) or a monster weapon shot's
    // (0x141b6e1d0, the spray's key). Its fanned target moves by two draws of 0..100 (the first in y), each /5 − 10
    // truncated, then stretches from its launch cell to its range, truncated per axis. A flame or a weapon shot turns
    // instead by its nonzero turn (a flame's FlameTurn; a weapon shot's 0.5 at variation 8, else 0.2): 100 units toward
    // the target plus each draw − 50 truncated times the turn, both rounded.
    public static (ProjectileVector End, float Speed) SeededEnd(ProjectileVector target, ProjectileVector launch, uint[] key,
        float range, bool speedDraw = false, float turn = 0)
    {
        var random = new AnimationVariantSelector.NativeRandom(key);
        float speed = speedDraw ? (random.Range(0, 100) * .8f + 60) / 100 : 1;
        float a = random.Range(0, 100), b = random.Range(0, 100);
        int px = (int)launch.X, py = (int)launch.Y;
        int vx = (int)target.X + (int)(b / 5 - 10) - px, vy = (int)target.Y + (int)(a / 5 - 10) - py;
        if (turn != 0)
        {
            int dx = (int)target.X - px, dy = (int)target.Y - py;
            float unit = dx == 0 && dy == 0 ? 0 : 100 / MathF.Sqrt(dx * dx + dy * dy);
            (vx, vy) = (RoundHalfUp(dx * unit) + RoundHalfUp((int)(b - 50) * turn),
                RoundHalfUp(dy * unit) + RoundHalfUp((int)(a - 50) * turn));
        }
        float length = MathF.Sqrt(vx * vx + vy * vy);
        if (length >= .01f) (vx, vy) = ((int)(vx * (range / length)), (int)(vy * (range / length)));
        return (new(px + vx, py + vy), speed);
    }

    /// <summary>An incinerate flame's turn by its stage (0x141b85a60): 0.1, 0.2, 0.35 or 0.45 as it reaches 4, 8 and 12.</summary>
    public static float FlameTurn(int stage) => stage < 4 ? .1f : stage < 8 ? .2f : stage < 12 ? .35f : .45f;

    static int RoundHalfUp(float value) => (int)MathF.Floor(value + .5f);

    // An ice elemental's spear end (0x141a09640, key [sequence, derived, repeats left, earlier releases]): 150 units toward
    // its target, rounded, plus the mean of one or three pairs of draws of −36..36 (three unless a 0..2 draw is 0; x
    // before y in each pair), truncated; then stretched to 150 from the launch cell, truncated per axis.
    public static ProjectileVector IceElementalSpearEnd(ProjectileVector target, ProjectileVector launch, uint[] key)
    {
        var random = new AnimationVariantSelector.NativeRandom(key);
        int n = random.Uniform(0, 2) != 0 ? 3 : 1;
        float sx = 0, sy = 0;
        for (int i = 0; i < n; i++)
        {
            sx += random.Range(-36, 36);
            sy += random.Range(-36, 36);
        }
        int px = (int)launch.X, py = (int)launch.Y, dx = (int)target.X - px, dy = (int)target.Y - py;
        float unit = dx == 0 && dy == 0 ? 0 : 150 / MathF.Sqrt(dx * dx + dy * dy);
        int vx = RoundHalfUp(dx * unit) + (int)(sx / n), vy = RoundHalfUp(dy * unit) + (int)(sy / n);
        float length = MathF.Sqrt(vx * vx + vy * vy);
        if (length > .01f) (vx, vy) = ((int)(vx * (150 / length)), (int)(vy * (150 / length)));
        return new(px + vx, py + vy);
    }

    // A Breach barrage projectile's end (0x141a3c940, the weapon shot's key): 150 cells from the caster's cell at a draw of
    // −55..55 degrees about its facing (atan2 of its line plus π/2, the line (sin, −cos)), truncated per axis. The aim is
    // not read.
    public static ProjectileVector BreachBarrageEnd(ProjectileVector caster, float facing, uint[] key)
    {
        int k = new AnimationVariantSelector.NativeRandom(key).Uniform(0, 110);
        float a = (k - 55) * 3.1415927f / 180f + facing + 3.1415927f;
        return new((int)caster.X - (int)(MathF.Sin(a) * 150f), (int)caster.Y - (int)(MathF.Cos(a) * -150f));
    }

    // The spell form's mover (0x141a10d50) runs the same steps, with mortar_projectile_distance_override between the
    // minimum range and the scatter: the landing rescaled to that distance from the launch (both forms, 0x141b6a017).
    // The first draw is the speed variation the spell form's flight time takes.
    // A spirit mortar's mover (0x141a09390) draws its scatter from a second key (scatterKey) instead.
    public static ProjectileVector MonsterMortarTarget(ProjectileVector actor, ProjectileVector origin,
        ProjectileVector aim, int minimumRange, int spread, ushort sequence, uint owner, int index, int distanceOverride,
        out float speedDraw, uint[]? scatterKey = null)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, (uint)(index + 2), owner, 0, 0, 0, 0, 0]);
        speedDraw = random.Range(0, 2);
        int x = (int)aim.X, y = (int)aim.Y, ax = (int)actor.X, ay = (int)actor.Y;
        int dx = x - ax, dy = y - ay;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < minimumRange)
        {
            if (length == 0)
            {
                float angle = random.Range(0, 6.2831855f);
                x = ax + RoundHalfUp(MathF.Sin(angle) * minimumRange);
                y = ay - RoundHalfUp(MathF.Cos(angle) * minimumRange);
            }
            else
            {
                float scale = minimumRange / length;
                x = ax + RoundHalfUp(dx * scale); y = ay + RoundHalfUp(dy * scale);
            }
        }
        int ox = (int)origin.X, oy = (int)origin.Y;
        if (distanceOverride > 0 && (x != ox || y != oy))
        {
            float scale = distanceOverride / MathF.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
            x = ox + RoundHalfUp((x - ox) * scale); y = oy + RoundHalfUp((y - oy) * scale);
        }
        if (spread <= 0) return new(x, y);
        if (scatterKey is not null) random = new(scatterKey);
        dx = x - ox; dy = y - oy;
        if (dx * dx + dy * dy >= spread * spread) return Scatter(ref random, x, y, spread);
        // The close-target branch samples the two chords from the launch cell, rather than scattering behind it.
        if (dx == 0 && dy == 0) dx = 1;
        float factor = spread / MathF.Sqrt(dx * dx + dy * dy);
        int bx = ox + RoundHalfUp(dx * factor), by = oy + RoundHalfUp(dy * factor);
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
        return new(ox + RoundHalfUp(ux * u) + RoundHalfUp(vx * v), oy + RoundHalfUp(uy * u) + RoundHalfUp(vy * v));
    }

    // A point uniform in the disc of the spread's radius about (x, y): an angle, then the radius's square.
    static ProjectileVector Scatter(ref AnimationVariantSelector.NativeRandom random, int x, int y, float spread)
    {
        float angle = random.Range(0, 6.2831855f);
        float radius = MathF.Sqrt(random.Range(0, spread * spread));
        return new(x + (int)(MathF.Sin(angle) * radius), y - (int)(MathF.Cos(angle) * radius));
    }

    /// <summary>A Labyrinth spirit mortar's launch (0x141a09390): 5 cells out along its line.</summary>
    public static ProjectileVector SpiritMortarLaunch(ProjectileVector origin, ProjectileVector aim)
    {
        var moved = origin + ((aim - origin).Length > 0 ? (aim - origin).Unit : new ProjectileVector(0, -1)) * 5;
        return new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
    }

    /// <summary>A Labyrinth spirit mortar's landing (0x141a09390): the mortar mover's steps from its launch with the
    /// minimum range measured from the caster, its scatter drawn from the second key [sequence, index + 2, owner, 0]
    /// (the index is the attack form's, not traced for this class).</summary>
    public static ProjectileVector SpiritMortarTarget(ProjectileVector actor, ProjectileVector launch, ProjectileVector aim,
        int minimumRange, int spread, ushort sequence, uint owner, int index, out float speedDraw) =>
        MonsterMortarTarget(actor, launch, aim, minimumRange, spread, sequence, owner, index, 0, out speedDraw,
            [sequence, (uint)(index + 2), owner, 0]);

    /// <summary>An in-line mortar's landing (0x141a15550, Breach and Kitava): max(15, d − in front) cells from the caster
    /// along its line to the target, scattered uniformly within the spread (cells). Its key's order is not traced: the
    /// server's object takes the mortar mover's key, whose first draw is the speed draw as the client's.</summary>
    public static ProjectileVector InLineMortarTarget(ProjectileVector actor, ProjectileVector aim, double inFront, float spread,
        ushort sequence, uint owner, int index, out float speedDraw)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, (uint)(index + 2), owner, 0, 0, 0, 0, 0]);
        speedDraw = random.Range(0, 2);
        var line = aim - actor;
        var landed = actor + (line.Length > 0 ? line.Unit : new ProjectileVector(0, -1)) * Math.Max(15, line.Length - inFront);
        return spread > 0 ? Scatter(ref random, RoundHalfUp((float)landed.X), RoundHalfUp((float)landed.Y), spread)
            : new(Math.Floor(landed.X + .5), Math.Floor(landed.Y + .5));
    }

    /// <summary>A ground mortar's landing (mortar_with_ground_effects): the aim scattered within the spread by the mortar
    /// mover's draws (the minimum range is not read), the aim itself where that cell is not walkable, then put
    /// <c>projectile_distance_override</c> cells from its launch on the same line. The client retries the scatter up to 10
    /// times and keys it by the action's release counter too; the server's object takes one draw on the mortar's key.</summary>
    public static ProjectileVector GroundMortarTarget(ClientState conn, ProjectileVector actor, ProjectileVector launch,
        ProjectileVector aim, int spread, int distanceOverride, ushort sequence, uint owner, int index, out float speedDraw)
    {
        var landed = MonsterMortarTarget(actor, launch, aim, 0, spread, sequence, owner, index, 0, out speedDraw);
        if (conn.Layout.Walkable is { } walk && !walk((int)landed.X, (int)landed.Y)) landed = aim;
        var line = landed - launch;
        if (distanceOverride <= 0 || line.Length <= 0) return landed;
        var moved = launch + line.Unit * distanceOverride;
        return new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
    }

    /// <summary>A ring of mortars around the caster (0x141a12620): one seeded base angle, projectile i turned i × 2π / n
    /// from it, each at its own distance from the minimum range to the spread (world units, so × 0.092 cells). The key's
    /// order is not traced.</summary>
    public static ProjectileVector[] AroundCasterTargets(ProjectileVector caster, int count, int minimum, int spread,
        ushort sequence, uint owner, uint ordinal)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, ordinal, 0, owner]);
        float start = random.Range(0, 6.2831855f);
        var targets = new ProjectileVector[count];
        for (int i = 0; i < count; i++)
        {
            float angle = start + i * (6.28318f / count), radius = random.Range(minimum, Math.Max(minimum, spread)) * .092f;
            targets[i] = new((int)caster.X + (int)(MathF.Sin(angle) * radius), (int)caster.Y - (int)(MathF.Cos(angle) * radius));
        }
        return targets;
    }

    /// <summary>A storm burst's orb headings in release order, absolute, each the line (cos h, −sin h). A nova of several
    /// (0x141c68140, key [sequence, owner, 0]): a draw in [0, 2π), orb i at that plus i × 2π/n plus a draw in ±π/(2n), the
    /// list then shuffled forward (each place swapped with a later or itself). Otherwise (0x141c679b0, key [sequence, 0,
    /// 0]): the facing turned by the mean of three draws in ±0.4363323.</summary>
    public static float[] StormBurstHeadings(float facing, int count, bool nova, ushort sequence, uint owner)
    {
        var headings = new float[count];
        if (!nova || count < 2)
        {
            var random = new AnimationVariantSelector.NativeRandom([sequence, 0, 0]);
            float turn = (random.Range(-.4363323f, .4363323f) + random.Range(-.4363323f, .4363323f) + random.Range(-.4363323f, .4363323f)) / 3;
            Array.Fill(headings, facing + turn);
            return headings;
        }
        var spread = new AnimationVariantSelector.NativeRandom([sequence, owner, 0]);
        float first = spread.Range(0, 6.2831855f), jitter = 3.1415927f / (2 * count);
        for (int i = 0; i < count; i++) headings[i] = first + i * (6.2831855f / count) + spread.Range(-jitter, jitter);
        for (int i = 0; i < count; i++)
        {
            int j = i + spread.Uniform(0, count - 1 - i);
            (headings[i], headings[j]) = (headings[j], headings[i]);
        }
        return headings;
    }

    /// <summary>A riptide's end (0x141b39750): 120 cells from the caster's cell toward its target (each part rounded half
    /// up), at skill_behaviour_variation 1 turned by a seeded angle in [−π/4, π/4], cut where the line first meets a cell
    /// the caster cannot stand on (size: its collision size plus one). The seed's last words are not traced.</summary>
    public static ProjectileVector RiptideEnd(ClientState conn, ProjectileVector caster, ProjectileVector target, bool turned,
        uint[] key, int size)
    {
        var line = (target - caster).Length > 0 ? (target - caster).Unit : new ProjectileVector(1, 0);
        var u = new ProjectileVector(Math.Floor(line.X * 120 + .5), Math.Floor(line.Y * 120 + .5));
        if (turned)
        {
            double a = new AnimationVariantSelector.NativeRandom(key).Range(-MathF.PI / 4, MathF.PI / 4);
            u = new(Math.Floor(u.X * Math.Cos(a) - u.Y * Math.Sin(a) + .5), Math.Floor(u.X * Math.Sin(a) + u.Y * Math.Cos(a) + .5));
        }
        // The client's line (0x141d87160): the wave stops at the last open cell before one it cannot cross.
        (int X, int Y) start = ((int)caster.X, (int)caster.Y), end = ((int)(caster.X + u.X), (int)(caster.Y + u.Y));
        return GameServer.Core.World.ClientPathfinder.LineBelow(start, end,
            (x, y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y)) ? 1 : 0, 1, out var last)
            ? new ProjectileVector(last.X, last.Y) : new ProjectileVector(end.X, end.Y);
    }
}

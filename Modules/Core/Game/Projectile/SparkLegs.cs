using GameServer.Core.Projectiles;

/// <summary>
/// Spark's wander, as the client draws its own sparks (3.29.3b, Spark action vtable 0x14350ca90). Each spark seeds the
/// client's small generator (0x14146d8f0, TinyMT32's key initializer) from [use sequence, 2 + its index in the volley,
/// the cast's repeats left (+0x2d0, SpellRepeats), owner id] (Frida 2026-10-31: 524 sparks). Its first leg (0x141b35a10) draws its life, a 15–25-cell length toward
/// its aim point and a 0.8–1.2 speed factor; each later leg (0x141b358c0) draws a turn of ±45°, or of 0–360° after
/// terrain stopped it (0x141b35e80), a length (0x141b360f0) and a speed factor (0x141b36080). Every draw is the
/// client's float range (0x1404ce3f0), all in single precision as the client computes it.
/// </summary>
sealed class SparkLegs : IProjectileLegs
{
    AnimationVariantSelector.NativeRandom random;
    readonly double baseSpeed;
    readonly ushort sequence;
    readonly int index;
    float heading;

    public SparkLegs(ushort sequence, int index, uint owner, double lifeSeconds, int randomDurationPercent, double baseSpeed,
        uint repeatsLeft = 0)
    {
        random = new([sequence, (uint)(index + 2), repeatsLeft, owner]);
        this.sequence = sequence; this.index = index;
        this.baseSpeed = baseSpeed;
        float life = (float)lifeSeconds, spread = life * Math.Min(randomDurationPercent, 100) / 100f;
        Lifetime = Draw(life - spread, life + spread);
    }

    public double Lifetime { get; }

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        float length = Draw(15, 25);
        var (x, y) = Cell(from);
        int dx = (int)Math.Floor(aim.X) - x, dy = (int)Math.Floor(aim.Y) - y;
        if (dx == 0 && dy == 0) dx = 1;
        float scale = length / MathF.Sqrt(dx * dx + dy * dy);
        heading = MathF.Atan2(dy, dx);
        var leg = new ProjectileLeg(new(x + (int)MathF.Round(dx * scale), y + (int)MathF.Round(dy * scale)), Speed());
        Console.WriteLine($"[gateway] spark seq={sequence} index={index} at=({x}, {y}) aim=({x + dx}, {y + dy}) first={leg.Target}");
        return leg;
    }

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        const float Turn = 45 * 0.017453292f, Tau = 6.2831855f;
        heading += blocked ? Draw(0, Tau) : Draw(-Turn, Turn);
        while (heading < 0) heading += Tau;
        while (heading > Tau) heading -= Tau;
        float length = Draw(15, 25);
        var (x, y) = Cell(from);
        return new(new(x + (int)(MathF.Cos(heading) * length), y + (int)(MathF.Sin(heading) * length)), Speed());
    }

    double Speed() => Draw(.8f, 1.2f) * (float)baseSpeed;

    // The client's cell is its integer position; a leg that arrived sits exactly on its target.
    static (int X, int Y) Cell(ProjectileVector p) => ((int)Math.Floor(p.X + 1e-6), (int)Math.Floor(p.Y + 1e-6));

    float Draw(float lo, float hi) => random.Range(lo, hi);
}

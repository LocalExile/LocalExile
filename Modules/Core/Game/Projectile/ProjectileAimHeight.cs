using GameServer.Core.Projectiles;


/// <summary>
/// 1:1 model of the 3.29.3c client's 3D projectile height ray (<c>0x141ba7c30</c> → <c>0x141bac460</c> →
/// <c>0x141bb31a0</c> → <c>0x141bb5570</c> → <c>0x141e35a50</c>).
/// <para>
/// Outbound: samples terrain height at <c>origin</c> and at <c>aim</c> (clamped to at least 60 world units =
/// <see cref="MinAimCells"/> from <c>origin</c>), launches at <c>terrain(origin) + launchSteps</c>, and slopes
/// linearly at <c>(terrain(aim) - terrain(origin)) / sampledDist</c> along the projectile's flight direction.
/// </para>
/// <para>
/// Return: keeps the projectile's current height at turnaround <c>S</c> and slopes linearly at
/// <c>(terrain(anchor) - terrain(S)) / |anchor - S|</c> so at the owner <c>D</c> its height above terrain equals
/// its height above terrain at <c>S</c>.
/// </para>
/// <para>
/// Terrain collision (<c>0x141e35a50</c>): explicit-height projectiles collide with a cell when the truncated
/// integer projectile height marker drops strictly below <c>terrain(x, y)</c> (<c>(int)projSteps &lt; terrain</c>).
/// Heights are TerrainHeights steps (7.8125 world units, larger is higher).
/// </para>
/// </summary>
public static class ProjectileAimHeight
{
    /// <summary>Minimum aim distance before sampling aim terrain height (<c>0x141ba7c30</c>: 60 world units).</summary>
    public const double MinAimCells = 60.0 / WorldUnits.PerCell;

    /// <summary>Default spell launch height (<c>0x141bb7110</c>: -68.0 world units / -7.8125 ≈ 8 steps).</summary>
    public const int SpellLaunchSteps = 8;

    static int Cell(double v) => (int)Math.Floor(v + .5);

    public readonly record struct Ray(ProjectileVector Origin, ProjectileVector Direction, double StartSteps, double SlopePerCell)
    {
        /// <summary>Applies the cast's pitch slope to an individual projectile leg (e.g. a fan or Nimis random direction).</summary>
        public Ray WithLeg(ProjectileVector origin, ProjectileVector direction) =>
            new(origin, direction.Unit, StartSteps, SlopePerCell);

        /// <summary>Projectile height in steps at <paramref name="point"/> projected along this leg.</summary>
        public double StepsAt(ProjectileVector point) =>
            StartSteps + SlopePerCell * Math.Max(0, (point - Origin).Dot(Direction));

        /// <summary>Integer height marker (truncated toward zero, matching <c>0x141bac460</c>/<c>0x141bb5570</c>).</summary>
        public ProjectileHeight MarkerAt(ProjectileVector point) => new((int)Math.Truncate(StepsAt(point)));

        /// <summary>Client <c>0x141e35a50</c> terrain-height check: blocked when truncated height drops below terrain.</summary>
        public bool Blocks(int x, int y, Func<int, int, int?> height)
        {
            if (height(x, y) is not int terrain) return false;
            double d = Math.Max(0, (x - Origin.X) * Direction.X + (y - Origin.Y) * Direction.Y);
            return (int)Math.Truncate(StartSteps + SlopePerCell * d) < terrain;
        }
    }

    /// <summary>Builds the outbound 3D height ray from <paramref name="origin"/> aimed at <paramref name="aim"/>,
    /// or null when either terrain height is unknown.</summary>
    public static Ray? Primary(ProjectileVector origin, ProjectileVector aim, int launchSteps, Func<int, int, int?> height)
    {
        if (height(Cell(origin.X), Cell(origin.Y)) is not int startGround) return null;
        var delta = aim - origin;
        double dist = delta.Length;
        var sampledAim = dist >= MinAimCells ? aim : origin + delta.Unit * MinAimCells;
        double sampledDist = Math.Max(dist, MinAimCells);
        if (height(Cell(sampledAim.X), Cell(sampledAim.Y)) is not int aimGround) return null;
        return new(origin, delta.Unit, startGround + launchSteps, (aimGround - startGround) / sampledDist);
    }

    /// <summary>Builds the return 3D height ray (<c>0x141bb31a0</c> → <c>0x141bb5570</c>) from turnaround
    /// <paramref name="from"/> toward <paramref name="anchor"/>, preserving <paramref name="currentSteps"/> at turnaround.</summary>
    public static Ray? Return(ProjectileVector from, ProjectileVector anchor, double currentSteps, Func<int, int, int?> height)
    {
        if (height(Cell(from.X), Cell(from.Y)) is not int fromGround) return null;
        if (height(Cell(anchor.X), Cell(anchor.Y)) is not int anchorGround) return null;
        var delta = anchor - from;
        double dist = delta.Length;
        double slope = dist >= 0.01 ? (anchorGround - fromGround) / dist : 0;
        return new(from, delta.Unit, currentSteps, slope);
    }
}

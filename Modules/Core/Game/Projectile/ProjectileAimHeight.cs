using GameServer.Core.Projectiles;


/// <summary>
/// Where a projectile is aimed decides how high it flies. A player's shot flies at the higher of its release cell's ground
/// and the aimed cell's ground; terrain that rises more than <see cref="ClearanceSteps"/> above that stops it
/// (ProjectileEndReason.Terrain), as a wall does. Aimed at the low ground in front of a cliff, the shot hits the cliff face;
/// aimed at the walkable top, it flies over the face. Shooting down never meets rising terrain.
/// <para>
/// Generated levels leave cliff faces open in the projectile grid (a 1_1_2 face rises 2 → 13 → 23 steps over two cells
/// with no blocking cell), so without this every arrow flew over them. LocalExile model v1, from the user's observation of
/// the live game (2026-10-30), not from the client's collision code: the clearance is a development value and the flight
/// is level, not a measured arc. Heights are TerrainHeights steps (7.8125 world units, larger is higher).
/// </para>
/// </summary>
static class ProjectileAimHeight
{
    /// <summary>How far terrain may rise above the flight's ground before it stops the shot (93.75 world units, below the
    /// Ranger's 16-step bow release).</summary>
    public const int ClearanceSteps = 12;

    static int Cell(double v) => (int)Math.Floor(v + .5);

    /// <summary>The ground a shot from <paramref name="origin"/> aimed at <paramref name="aim"/> flies over, or null when
    /// either height is unknown (no capture or generated level): the shot then keeps the plain projectile grid.</summary>
    public static int? Ground(ProjectileVector origin, ProjectileVector aim, Func<int, int, int?> height) =>
        height(Cell(origin.X), Cell(origin.Y)) is int from && height(Cell(aim.X), Cell(aim.Y)) is int to ? Math.Max(from, to) : null;

    /// <summary>Whether cell (x, y) rises too far above <paramref name="ground"/> for the shot to pass.</summary>
    public static bool Blocks(int x, int y, int ground, Func<int, int, int?> height) =>
        height(x, y) is int h && h > ground + ClearanceSteps;
}

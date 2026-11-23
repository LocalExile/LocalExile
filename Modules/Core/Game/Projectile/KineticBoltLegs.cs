using GameServer.Core.Projectiles;

/// <summary>
/// Kinetic Bolt's zig-zag trajectory and secondary projectile splitting, matching 3.29.3c client ZigZagProjectile
/// (vtable 0x1434ec230: 0x141a8d280 initial leg, 0x141a8cdb0/0x141a8ce60 turns, 0x141a8ca50 secondary release).
/// Leg 0 flies 15 cells along the launch ray; each turn (up to <paramref name="maxTurns"/>) occurs on leg end,
/// non-evaded enemy contact, or terrain contact before Pierce/Chain/Return.
/// </summary>
sealed class KineticBoltLegs : IProjectileLegs
{
    public const double FirstLegCells = 15;
    public const double SecondLegCells = 10;
    public const double LaterLegCells = 20;
    const float HalfPi = 1.5707964f;

    readonly ushort sequence;
    readonly int volleyOffset;
    readonly int maxTurns;
    readonly double speed;
    readonly double maxRange;
    readonly ProjectileProfile secondaryProfile;
    float heading;
    int turnCount;

    public KineticBoltLegs(ushort sequence, int volleyOffset, int maxTurns, double speed, double maxRange,
        ProjectileProfile secondaryProfile, int turnCount = 0, float heading = 0)
    {
        this.sequence = sequence;
        this.volleyOffset = volleyOffset;
        this.maxTurns = Math.Max(0, maxTurns);
        this.speed = speed;
        this.maxRange = maxRange;
        this.secondaryProfile = secondaryProfile;
        this.turnCount = Math.Max(0, turnCount);
        this.heading = heading;
    }

    public double Lifetime => Math.Max(10.0, (maxTurns + 2) * LaterLegCells / Math.Max(1.0, speed) + 10.0);
    public bool TurnsOnEnemy => turnCount < maxTurns;
    public bool EndsAfterLegs => true;

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        var delta = aim - from;
        if (delta.Length > 0)
            heading = MathF.Atan2((float)delta.X, -(float)delta.Y);
        double length = Math.Min(maxRange, turnCount == 0 ? FirstLegCells : LaterLegCells);
        return new(from + Direction(heading) * length, speed);
    }

    public ProjectileLeg Next(ProjectileVector from, bool blocked) =>
        NextTurn(from, blocked)?.Leg ?? new(from + Direction(heading) * Math.Min(maxRange, LaterLegCells), speed);

    public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked)
    {
        if (turnCount >= maxTurns) return null;
        int parity = (sequence + volleyOffset + turnCount) & 1;
        int sign = parity * 2 - 1;
        float turn90 = sign * HalfPi;
        float secondaryAngle;
        double legCells;
        if (turnCount == 0)
        {
            secondaryAngle = heading - turn90 * 0.5f;
            heading = secondaryAngle + turn90;
            legCells = SecondLegCells;
        }
        else
        {
            secondaryAngle = heading;
            heading = secondaryAngle + turn90;
            legCells = LaterLegCells;
        }
        turnCount++;
        double length = Math.Min(maxRange, legCells);
        return new(new(from + Direction(heading) * length, speed), secondaryProfile, Direction(secondaryAngle));
    }

    public IProjectileLegs Continue(ProjectileVector direction) =>
        new KineticBoltLegs(sequence, volleyOffset, maxTurns, speed, maxRange, secondaryProfile,
            turnCount, MathF.Atan2((float)direction.X, -(float)direction.Y));

    static ProjectileVector Direction(float angle) => new(MathF.Sin(angle), -MathF.Cos(angle));
}

using GameServer.Core.Projectiles;

/// <summary>
/// A straight flight whose speed changes evenly: the Shaper's accelerating projectiles (mover 0x1419f6030, frame hook
/// 0x1419f6180: covered(t) = v0·t + a·t²/2 from the release toward the aim, a = 50 cells/s²) and the decelerating
/// projectiles that stop where their speed runs out (0x141acbd90, frame hook 0x141acc170: a negative a, the flight ending
/// at v0/|a|). Each leg flies one <see cref="Step"/> of it at that step's average speed, so every leg ends on the
/// client's curve; an accelerating one ends on the profile's leash at its range, a decelerating one where it stops.
/// </summary>
public sealed class AcceleratingLegs(double startSpeed, double acceleration, double range) : IProjectileLegs
{
    /// <summary>Seconds of flight each leg covers.</summary>
    public const double Step = .05;

    ProjectileVector start, direction;
    int step;

    bool Slows => acceleration < 0;
    // When a decelerating flight stops.
    double StopsAt => startSpeed / -acceleration;

    public bool EndsAfterLegs => Slows;

    /// <summary>Until it has covered its range (or stopped), and a little longer.</summary>
    public double Lifetime => (Slows ? StopsAt
        : acceleration > 0 ? (-startSpeed + Math.Sqrt(startSpeed * startSpeed + 2 * acceleration * range)) / acceleration
        : range / Math.Max(.001, startSpeed)) + .5;

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        start = from;
        direction = (aim - from).Length > 0 ? (aim - from).Unit : new(1, 0);
        return Next(from, blocked: false);
    }

    /// <summary>A wall ends it: no turn; so does a decelerating flight's stop.</summary>
    public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked) =>
        blocked || Slows && step * Step >= StopsAt ? null : new ProjectileLegTurn(Next(from, blocked));

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        double Covered(double t) => startSpeed * t + acceleration * t * t / 2;
        double t0 = step * Step, t1 = ++step * Step;
        if (Slows) (t0, t1) = (Math.Min(t0, StopsAt), Math.Min(t1, StopsAt));
        double before = Covered(t0), after = Covered(t1);
        return new(start + direction * after, Math.Max(.001, (after - before) / Math.Max(1e-6, t1 - t0)));
    }
}

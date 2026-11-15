using GameServer.Core.Projectiles;

/// <summary>
/// A straight flight that speeds up evenly as the client draws the Shaper's accelerating projectiles (mover 0x1419f3620,
/// frame hook 0x1419f3770: covered(t) = v0·t + a·t²/2 from the release toward the aim). Each leg flies one
/// <see cref="Step"/> of it at that step's average speed; the profile's leash ends it at its range.
/// </summary>
public sealed class AcceleratingLegs(double startSpeed, double acceleration, double range) : IProjectileLegs
{
    /// <summary>Seconds of flight each leg covers.</summary>
    public const double Step = .05;

    ProjectileVector start, direction;
    int step;

    /// <summary>Until it has covered its range, and a little longer.</summary>
    public double Lifetime => (acceleration > 0
        ? (-startSpeed + Math.Sqrt(startSpeed * startSpeed + 2 * acceleration * range)) / acceleration
        : range / Math.Max(.001, startSpeed)) + .5;

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        start = from;
        direction = (aim - from).Length > 0 ? (aim - from).Unit : new(1, 0);
        return Next(from, blocked: false);
    }

    /// <summary>A wall ends it: no turn.</summary>
    public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked) => blocked ? null : new ProjectileLegTurn(Next(from, blocked));

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        double Covered(double t) => startSpeed * t + acceleration * t * t / 2;
        double before = Covered(step * Step), after = Covered(++step * Step);
        return new(start + direction * after, Math.Max(.001, (after - before) / Step));
    }
}

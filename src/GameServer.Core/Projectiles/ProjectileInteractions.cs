namespace GameServer.Core.Projectiles;

public enum ProjectileInteractionKind { Augment, Solid, ForceWall }

/// <summary>Dynamic projectile surface, separate from actor walkability and damage targets.
/// The host owns lifetime and owner filters; capacity is consumed atomically in event order.</summary>
public sealed record ProjectileInteraction(long Id, ProjectileInteractionKind Kind, ProjectileVector Start, ProjectileVector End)
{
    public int? Team { get; init; }
    public int Capacity { get; set; } = int.MaxValue;
    public double DamageMultiplier { get; init; } = 1;
}

public sealed partial class ProjectileRuntime
{
    public List<ProjectileInteraction> Interactions { get; } = [];

    static double? Crossing(ProjectileVector from, ProjectileVector to, ProjectileInteraction wall)
    {
        static double Cross(ProjectileVector a, ProjectileVector b) => a.X * b.Y - a.Y * b.X;
        var r = to - from; var s = wall.End - wall.Start;
        double denominator = Cross(r, s);
        if (Math.Abs(denominator) < 1e-12) return null;
        double t = Cross(wall.Start - from, s) / denominator;
        double u = Cross(wall.Start - from, r) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? t : null;
    }

    void Interact(ProjectileEntity p, ProjectileInteraction wall, IReadOnlyList<ProjectileActor> actors)
    {
        p.Interactions.Add(wall.Id);
        if (wall.Capacity <= 0) return;
        wall.Capacity--;
        p.DamageMultiplier *= wall.DamageMultiplier;
        if (wall.Kind == ProjectileInteractionKind.Solid) End(p, ProjectileEndReason.Terrain);
        if (wall.Kind == ProjectileInteractionKind.ForceWall)
        {
            var along = wall.End - wall.Start;
            var normal = new ProjectileVector(-along.Y, along.X).Unit;
            var incomingSide = p.Direction.Dot(normal);
            var target = Nearby(p, actors).FirstOrDefault(a =>
                (a.Position(Now) - p.Position).Dot(normal) * incomingSide < 0);
            if (!p.Profile.CannotChain && target is not null)
            {
                p.Direction = (target.Position(Now) - p.Position).Unit;
                Publish(ProjectileEventKind.Redirect, p);
            }
            else End(p, ProjectileEndReason.Terrain);
        }
    }
}

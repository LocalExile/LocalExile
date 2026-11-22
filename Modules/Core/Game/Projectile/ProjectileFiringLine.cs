using System.Numerics;
using GameServer.Core.Projectiles;

/// <summary>World-space source and aim prepared by a projectile action before it distributes the shots.</summary>
internal readonly record struct ProjectileFiringLine(Vector2 From, Vector2 To)
{
    internal const float WorldPerCell = 10.869565f, CellsPerWorld = .092f;

    public static Vector2 World(ProjectileVector cell) =>
        new(((float)cell.X + .5f) * WorldPerCell, ((float)cell.Y + .5f) * WorldPerCell);

    public static ProjectileVector Cell(Vector2 world) =>
        new(MathF.Floor(world.X * CellsPerWorld), MathF.Floor(world.Y * CellsPerWorld));

    public static ProjectileFiringLine Prepare(ProjectileVector source, ProjectileVector aim, int offset, bool noOffset,
        float maximum, bool usesTargetLocation, Func<float> coincidentAngle)
    {
        if (aim == source)
        {
            var (sin, cos) = MathF.SinCos(coincidentAngle());
            aim = source + new ProjectileVector((int)(60 * sin), (int)(60 * cos));
        }
        var origin = World(source);
        var destination = World(aim);
        var delta = destination - origin;
        float distance = delta.Length();
        // Preparation extends a close aim to 60 world units before it moves the release point forward.
        if (distance < 60)
            destination = origin + delta * (60 / distance);
        else if (usesTargetLocation && distance > maximum * WorldPerCell)
            destination = origin + delta * (maximum * WorldPerCell / distance);

        var unit = Vector2.Normalize(destination - origin);
        var forward = noOffset ? Vector2.Zero : unit * (offset == 0 ? 30 : offset);
        if (!usesTargetLocation && offset != 0) destination += forward;
        return new(origin + forward, destination);
    }

    public ProjectileVector RangeEnd(float maximum)
    {
        var ray = To - From;
        return Cell(From + ray * (maximum * WorldPerCell / ray.Length()));
    }
}

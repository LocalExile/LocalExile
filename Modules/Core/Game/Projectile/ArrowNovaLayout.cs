using System.Numerics;
using GameServer.Core.Projectiles;

internal static class ArrowNovaLayout
{
    const float WorldPerCell = 10.869565f, CellsPerWorld = .092f;

    // TornadoShotArrow uses its target location. Its rain mothers stop at the dispatcher's integer
    // destinations, where each releases a secondary group. A generic arrow extends that initial ray.
    public static Func<ProjectileEntity, int, ProjectileFlightLayout?> Create(Func<string, int> get, ProjectileSweep? sweep,
        Game.ParallelFiringPoints? points, bool tornado, bool usesTargetLocation)
    {
        int offset = get("projectile_firing_forward_distance_override");
        int forward = offset == 0 ? 30 : offset;
        bool noOffset = get("projectiles_not_offset") != 0 || get("is_trap_object") != 0 || get("is_remote_mine") != 0;
        bool random = get("random_projectile_direction") != 0;
        float maximum = get("projectile_maximum_range_override") is > 0 and var range ? range
            : get("reclaimed_malevolence_notable_abyss_searching") != 0 ? 50 : 150;
        return (payload, count) =>
        {
            if (!tornado && count == 1) return null;
            var landing = Grid(payload.Position);
            var origin = World(landing);
            float Angle(int ordinal) => Game.CoincidentProjectileAngle(payload.Use.Sequence,
                GatewayProjectileSkillProfiles.DerivedByte(ordinal), payload.Use.RepeatsLeft, payload.Use.Owner.Id);
            ProjectileFlightPath Flight(Vector2 from, Vector2 to, int ordinal, ProjectileLaunch launch = default)
            {
                var start = Cell(from);
                var end = Cell(to);
                // The mover tests grid equality. A coincident endpoint uses the flying arrow's own seeded aim.
                if (start == end)
                {
                    var (sin, cos) = MathF.SinCos(Angle(ordinal));
                    to = from + new Vector2(sin, cos) * 652.173889f;
                    end = Cell(to);
                }
                if (!usesTargetLocation)
                {
                    var ray = to - from;
                    end = Cell(from + ray * (maximum * WorldPerCell / ray.Length()));
                }
                return new(start, end, launch);
            }
            if (sweep is not null && count > 1)
            {
                // Barrage normalizes the aim to 60 cells (+X when coincident), then truncates each rotated
                // component before converting it to world coordinates and applying the forward offset.
                var start = sweep.StartDirection(payload) * 60;
                var initial = new Vector2((float)Math.Floor(start.X + .5), (float)Math.Floor(start.Y + .5));
                float heading = MathF.Atan2(initial.Y, initial.X), step = (float)sweep.Radians / (count - 1);
                float radius = initial.Length();
                return (i, ordinal) =>
                {
                    var (sin, cos) = MathF.SinCos(heading + i * step);
                    var ray = World(new((int)(radius * cos), (int)(radius * sin)));
                    return Flight(origin + Vector2.Normalize(ray) * forward, origin + ray, ordinal);
                };
            }

            var aim = Grid(payload.Use.Aim);
            if (aim == landing)
            {
                var (sin, cos) = MathF.SinCos(Angle(payload.SpawnOrdinal));
                aim = landing + new ProjectileVector((int)(60 * sin), (int)(60 * cos));
            }
            var destination = World(aim);
            var delta = destination - origin;
            float distance = delta.Length();
            if (distance < 60)
                destination = origin + delta * (60 / distance);
            else if (usesTargetLocation && distance > maximum * WorldPerCell)
                destination = origin + delta * (maximum * WorldPerCell / distance);
            var unit = Vector2.Normalize(destination - origin);
            var startPoint = noOffset ? origin : origin + unit * forward;
            if (!usesTargetLocation && offset != 0 && !noOffset) destination += unit * forward;
            var basis = destination - startPoint;
            if (count == 1) return (_, ordinal) => Flight(startPoint, destination, ordinal);
            float rayLength = basis.Length();
            var radial = rayLength == 0 ? Vector2.Zero : basis * (1f / rayLength);
            if (points is null)
                return (i, ordinal) => Flight(startPoint, RadialEnd(startPoint, radial, rayLength, i * (6.28318f / count)), ordinal);

            var paths = new (Vector2 From, Vector2 To, ProjectileLaunch Launch)[count];
            int index = 0;
            var lateral = new Vector2(radial.Y, -radial.X);
            foreach (var (slot, size) in points.Slots(count))
            {
                var from = startPoint + lateral * (slot * (float)points.XDistance)
                    + radial * (Math.Abs(slot) * (float)points.YDistance);
                // Each subgroup normalizes its own translated pair before rotating the individual arrows.
                var deltaAtPoint = (slot == 0 ? destination : from + basis) - from;
                float length = deltaAtPoint.Length();
                var direction = length == 0 ? Vector2.Zero : deltaAtPoint * (1f / length);
                var rng = random ? Game.ParallelFiringPoints.GroupRandom(payload.Use,
                    GatewayProjectileSkillProfiles.DerivedByte(payload.SpawnOrdinal), slot) : default;
                var launch = points.Launch(slot);
                for (int i = 0; i < size; i++)
                {
                    float theta = random ? rng.Range(0, 6.2831855f) : i * (6.28318f / size);
                    paths[index++] = (from, RadialEnd(from, direction, usesTargetLocation ? length : 150, theta), launch);
                }
            }
            return (i, ordinal) => Flight(paths[i].From, paths[i].To, ordinal, paths[i].Launch);
        };
    }

    static Vector2 RadialEnd(Vector2 origin, Vector2 direction, float length, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        var ray = new Vector2(direction.X * cos - direction.Y * sin, direction.Y * cos + direction.X * sin);
        return origin + ray * length;
    }

    static Vector2 World(ProjectileVector cell) =>
        new(((float)cell.X + .5f) * WorldPerCell, ((float)cell.Y + .5f) * WorldPerCell);

    static ProjectileVector Grid(ProjectileVector point) => new(Math.Floor(point.X + .5), Math.Floor(point.Y + .5));

    static ProjectileVector Cell(Vector2 world) =>
        new(MathF.Floor(world.X * CellsPerWorld), MathF.Floor(world.Y * CellsPerWorld));
}

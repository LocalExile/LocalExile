using System.Numerics;
using GameServer.Core.World;

/// <summary>The landing cells of one multi-mine Locus throw, in release order.</summary>
internal sealed class LocusMineArc
{
    readonly (int X, int Y) from, aim;
    readonly float distance, firstAngle, step;
    const float WorldPerCell = 10.869565f;

    public LocusMineArc((int X, int Y) from, (int X, int Y) aim, int count, bool reverse)
    {
        this.from = from;
        this.aim = aim;
        var delta = new Vector2(aim.X - from.X, aim.Y - from.Y);
        distance = MathF.Max(20, delta.Length());
        // The action converts the integer displacement to a cell centre before taking its heading.
        var direction = CellCentre(delta.X, delta.Y);
        float heading = MathF.Atan2(direction.Y, direction.X) + MathF.PI / 2;
        if (heading < 0) heading += MathF.Tau;
        step = (100 * MathF.PI / 180) / (count - 1) * (reverse ? -1 : 1);
        firstAngle = heading - (count - 1) * step * .5f;
    }

    static Vector2 CellCentre(float x, float y) => new((x + .5f) * WorldPerCell, (y + .5f) * WorldPerCell);

    public (int X, int Y) Landing(int index, AreaLayout layout)
    {
        var (sin, cos) = MathF.SinCos(firstAngle + index * step);
        var target = (X: from.X + (int)MathF.Floor(distance * sin + .5f),
                      Y: from.Y + (int)MathF.Floor(-distance * cos + .5f));
        var start = from;
        if (from != aim)
        {
            var origin = CellCentre(from.X, from.Y);
            var end = CellCentre(aim.X, aim.Y);
            var direction = end - origin;
            float t = Vector2.Dot(CellCentre(target.X, target.Y) - origin, direction) / direction.LengthSquared();
            var projected = t >= 1 ? end : t <= 0 ? origin : origin + direction * t;
            start = ((int)MathF.Floor(projected.X * .092f), (int)MathF.Floor(projected.Y * .092f));
        }

        var sight = layout.Sight;
        // On open ground the ideal endpoint wins both terrain checks.
        if (InBounds(target) && layout.WalkableFor(target.X, target.Y, 0)
            && (sight is null || (!ClientPathfinder.LineBelow(start, target, sight, 1)
                && !ClientPathfinder.LineBelow(target, from, sight, 1)))) return target;

        // First sweep sideways from the projection on the aim line. With no visible landing, sweep from the thrower.
        return LastLanding(start, checkSight: true) ?? LastLanding(from, checkSight: false) ?? aim;

        bool InBounds((int X, int Y) cell) => cell.X >= 0 && cell.Y >= 0
            && (layout.GridW == 0 || (cell.X < layout.GridW && cell.Y < layout.GridH));

        (int X, int Y)? LastLanding((int X, int Y) begin, bool checkSight)
        {
            (int X, int Y)? last = null;
            foreach (var cell in ClientPathfinder.Line(begin, target))
            {
                if (!InBounds(cell) || (sight is not null && sight(cell.X, cell.Y) < 1)) break;
                if (layout.WalkableFor(cell.X, cell.Y, 0)
                    && (!checkSight || sight is null || !ClientPathfinder.LineBelow(cell, from, sight, 1))) last = cell;
            }
            return last;
        }
    }
}

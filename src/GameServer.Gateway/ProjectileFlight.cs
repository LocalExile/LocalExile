/// <summary>A straight projectile swept against observed monster movement, using elapsed game time.
/// Caller serializes observations with monster movement orders. Positions are the server's integer walk estimates.</summary>
sealed class ProjectileFlight
{
    public readonly record struct Target(uint Id, (int X, int Y) Position, object? Motion = null);
    public readonly record struct Step((double X, double Y) Position, double At, bool Finished, uint TargetId = 0, bool Wall = false);
    readonly (int X, int Y) start, end;
    readonly double launched, duration, radius;
    readonly bool wall;
    readonly HashSet<uint> rejected = [];
    Dictionary<uint, Target> previous;
    double sampledAt;
    Step step;

    public ProjectileFlight((int X, int Y) start, (int X, int Y) end, double speed, double launched,
        double radius, bool wall, IEnumerable<Target> targets)
    {
        if (!double.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        (this.start, this.end, this.launched, this.radius, this.wall) = (start, end, launched, radius, wall);
        duration = Math.Sqrt(Math.Pow((double)end.X - start.X, 2) + Math.Pow((double)end.Y - start.Y, 2)) / speed;
        sampledAt = launched;
        previous = targets.ToDictionary(t => t.Id);
        step = new(start, launched, false);
    }

    (double X, double Y) Position(double at)
    {
        double f = duration == 0 ? 1 : Math.Clamp((at - launched) / duration, 0, 1);
        return (start.X + ((double)end.X - start.X) * f, start.Y + ((double)end.Y - start.Y) * f);
    }

    // An optional attack accuracy check may reject contact without terminating flight. Each rejected object is tested
    // only once for this straight flight; accepted contact retains the ordinary terminal result. Spells omit it.
    public Step Advance(double now, IReadOnlyList<Target> targets, Func<uint, bool>? accept = null)
    {
        if (step.Finished || now <= sampledAt) return step; // A stopped game clock cannot hit or expire anything.
        if (duration == 0 && wall) return step = new(end, launched, true, Wall: true);
        double until = Math.Min(now, launched + duration);
        var from = Position(sampledAt);
        var to = Position(until);
        double fraction = 1;
        uint hit = 0;
        bool atEnd = until >= launched + duration;
        List<(double Fraction, uint Id)>? contacts = accept is null ? null : [];
        foreach (var target in targets)
        {
            if (rejected.Contains(target.Id)) continue;
            double? contact;
            if (previous.TryGetValue(target.Id, out var observed) && ReferenceEquals(observed.Motion, target.Motion))
            {
                var before = observed.Position;
                // If scheduling passed the end of the flight, only sweep the monster up to that same time.
                double f = Math.Clamp((until - sampledAt) / (now - sampledAt), 0, 1);
                var after = (X: before.X + ((double)target.Position.X - before.X) * f,
                    Y: before.Y + ((double)target.Position.Y - before.Y) * f);
                contact = Contact((from.X - before.X, from.Y - before.Y),
                    (to.X - after.X, to.Y - after.Y), radius);
            }
            else
            {
                // A newly observed/spawned object has no earlier trajectory: it cannot intercept a past segment.
                double x = to.X - target.Position.X, y = to.Y - target.Position.Y;
                contact = until == now && x * x + y * y <= radius * radius ? 1 : null;
            }
            if (contact is double c && !(wall && atEnd && c == 1))
            {
                if (contacts is not null) contacts.Add((c, target.Id));
                else if (c <= fraction && (c < fraction || hit == 0 || target.Id < hit))
                    (fraction, hit) = (c, target.Id);
            }
        }
        if (contacts is not null)
        {
            // Evaluate in swept contact order, not dictionary order; an evaded nearer monster must not hide a later
            // contact within the very same scheduler interval. Stable ids break coincident-contact ties.
            foreach (var candidate in contacts.OrderBy(c => c.Fraction).ThenBy(c => c.Id))
            {
                if (accept!(candidate.Id)) { (fraction, hit) = candidate; break; }
                rejected.Add(candidate.Id);
            }
        }
        double at = sampledAt + (until - sampledAt) * fraction;
        step = new(Position(at), at, hit != 0 || atEnd, hit, hit == 0 && atEnd && wall);
        sampledAt = until;
        previous = targets.ToDictionary(t => t.Id);
        return step;
    }

    // Earliest entry of relative projectile/monster movement into a circle, including tangencies and initial overlap.
    static double? Contact((double X, double Y) from, (double X, double Y) to, double radius)
    {
        double c = from.X * from.X + from.Y * from.Y - radius * radius;
        if (c <= 0) return 0;
        double x = to.X - from.X, y = to.Y - from.Y, a = x * x + y * y;
        if (a == 0) return null;
        double b = from.X * x + from.Y * y, discriminant = b * b - a * c;
        if (b >= 0 || discriminant < 0) return null;
        double t = (-b - Math.Sqrt(discriminant)) / a;
        return t is >= 0 and <= 1 ? t : null;
    }

    /// <summary>Clip the static route before the first blocked grid cell. Supercover traversal includes diagonal corners.</summary>
    public static ((int X, int Y) End, bool Wall) ClipWall((int X, int Y) from, (int X, int Y) to,
        Func<int, int, bool> blocked)
    {
        bool wall = false;
        while (true)
        {
            var clipped = TraceWall(from, to, blocked);
            wall |= clipped.Wall;
            if (!clipped.Wall || clipped.End == from) return (clipped.End, wall);
            // The wire endpoint is an integer cell. Rounding can change the line enough to meet an earlier
            // neighboring blocker; trace that actual glide until the retained segment is clear.
            to = clipped.End;
        }
    }

    static ((int X, int Y) End, bool Wall) TraceWall((int X, int Y) from, (int X, int Y) to,
        Func<int, int, bool> blocked)
    {
        int x = from.X, y = from.Y;
        if (blocked(x, y)) return (from, true);
        long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long ax = Math.Abs(dx), ay = Math.Abs(dy), boundaryX = 1, boundaryY = 1;
        // Boundary times are odd / (2 * axis distance), starting half a cell from the integer origin.
        // Compare them exactly: accumulated floating steps can split a true corner and miss a side cell.
        // Int128 also covers full signed-int coordinate spans without cross-product overflow.
        while ((sx != 0 && boundaryX <= 2 * ax) || (sy != 0 && boundaryY <= 2 * ay))
        {
            Int128 order = (Int128)boundaryX * ay - (Int128)boundaryY * ax;
            bool crossX = sx != 0 && (sy == 0 || order <= 0);
            bool crossY = sy != 0 && (sx == 0 || order >= 0);
            if ((crossX && blocked(x + sx, y)) || (crossY && blocked(x, y + sy))
                || (crossX && crossY && blocked(x + sx, y + sy)))
                return ((x, y), true); // The current integer cell is exactly the last open cell before entry.
            if (crossX) { x += sx; boundaryX += 2; }
            if (crossY) { y += sy; boundaryY += 2; }
        }
        return (to, false);
    }
}

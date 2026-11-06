/// <summary>A one-way rail walk: game time advances it only while escorted. Presentation can send the remaining leg
/// once, and interrupt it at Position when the escort leaves.</summary>
sealed class RailEscort(IReadOnlyList<(int X, int Y)> path, double speed)
{
    public IReadOnlyList<(int X, int Y)> Path { get; } = path.Count >= 2 && double.IsFinite(speed) && speed > 0 ? path : throw new ArgumentException("A rail escort needs a path and positive speed.");
    public double Speed { get; } = speed;
    public int Target { get; private set; } = 1;
    double along;
    public bool Complete => Target >= Path.Count;
    public (int X, int Y) Destination => Path[Math.Min(Target, Path.Count - 1)];
    public (double X, double Y) Position
    {
        get
        {
            if (Complete) return Path[^1];
            var a = Path[Target - 1]; var b = Path[Target];
            double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            double t = length == 0 ? 1 : along / length;
            return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
    }
    public double RemainingSeconds => Math.Sqrt(Math.Pow(Destination.X - Position.X, 2) + Math.Pow(Destination.Y - Position.Y, 2)) / Speed;

    public void Advance(double seconds, bool escorted)
    {
        if (!escorted || !double.IsFinite(seconds) || seconds <= 0) return;
        double distance = seconds * Speed;
        while (!Complete)
        {
            var a = Path[Target - 1]; var b = Path[Target];
            double left = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) - along;
            if (distance < left) { along += distance; return; }
            distance -= left; along = 0; Target++;
        }
    }

    /// <summary>Resume an already shown escort after its module reloads, at the closest point on its rail.</summary>
    public void Restore((int X, int Y) position)
    {
        double best = double.MaxValue;
        for (int i = 1; i < Path.Count; i++)
        {
            var a = Path[i - 1]; var b = Path[i];
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((position.X - a.X) * dx + (position.Y - a.Y) * dy) / length, 0, 1);
            double distance = Math.Pow(position.X - a.X - t * dx, 2) + Math.Pow(position.Y - a.Y - t * dy, 2);
            if (distance >= best) continue;
            (best, Target, along) = (distance, i, t * Math.Sqrt(length));
        }
        if (Math.Abs(Position.X - Destination.X) + Math.Abs(Position.Y - Destination.Y) < 1e-9) { Target++; along = 0; }
    }
}

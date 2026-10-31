namespace GameServer.Modules.Delve;

/// <summary>A bounded trip along fixed rails. Sulphite is apportioned by cumulative distance, including rounding residue.</summary>
sealed class DelveJourney
{
    readonly (int X, int Y)[] points;
    readonly double[] distances;
    public int Cost { get; }
    public double Travelled { get; private set; }
    public double Length => distances[^1];
    public bool Finished => Travelled >= Length;
    public int Charged { get; private set; }
    public int Depth { get; }
    int completed;
    public bool Completed => Volatile.Read(ref completed) != 0;
    public HashSet<uint> EncounterMonsters { get; } = [];
    public bool TryComplete(bool enemiesAlive) => Finished && !enemiesAlive && Interlocked.CompareExchange(ref completed, 1, 0) == 0;
    public float Heading(double travelled)
    {
        int i = 1;
        while (i < points.Length - 1 && distances[i] <= travelled) i++;
        while (i > 1 && points[i] == points[i - 1]) i--;
        var a = points[i - 1]; var b = points[i];
        // Native Positioned faces -Y at zero, +X at pi/2 (11-skills.md, Rotation).
        return MathF.Atan2(b.X - a.X, a.Y - b.Y);
    }

    // Separate stop/resume distances prevent repeated start/stop at the escort boundary.
    public static bool EscortMoving(bool moving, double separation, bool leading) => leading || separation <= (moving ? 90 : 65);
    public DelveJourney(IReadOnlyList<(int X, int Y)> route, int cost, int depth = 2)
    {
        if (route.Count < 2 || cost < 0 || cost > ushort.MaxValue) throw new ArgumentException("Invalid Delve journey");
        if (depth < 2) throw new ArgumentOutOfRangeException(nameof(depth));
        points = route.ToArray(); distances = new double[points.Length]; Cost = cost; Depth = depth;
        for (int i = 1; i < points.Length; i++) distances[i] = distances[i - 1] + DelveRailRoute.Distance(points[i - 1], points[i]);
        if (Length <= 0) throw new ArgumentException("Empty Delve journey");
    }

    public ((int X, int Y) At, float Rotation) At(double travelled)
    {
        travelled = Math.Clamp(travelled, 0, Length);
        int i = 1;
        while (i < points.Length - 1 && distances[i] < travelled) i++;
        var a = points[i - 1]; var b = points[i];
        double length = distances[i] - distances[i - 1];
        double t = length == 0 ? 1 : (travelled - distances[i - 1]) / length;
        return (((int)Math.Round(a.X + (b.X - a.X) * t), (int)Math.Round(a.Y + (b.Y - a.Y) * t)),
            Heading(travelled));
    }

    // Commit the debit before advancing. A failed save or insufficient balance leaves the journey unchanged.
    public bool Advance(double distance, Func<int, bool> debit)
    {
        if (!double.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        double next = Math.Min(Length, Travelled + distance);
        int charged = next >= Length ? Cost : (int)Math.Floor(Cost * next / Length);
        int delta = charged - Charged;
        if (delta > 0 && !debit(delta)) return false;
        Travelled = next; Charged = charged;
        return true;
    }
}

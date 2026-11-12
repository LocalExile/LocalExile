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
    public HashSet<uint> DestinationMonsters { get; } = [];
    public const int FinaleWaveCount = 3;
    public const double FinaleDuration = 5;
    public double FinaleElapsed { get; private set; }
    public int WavesScheduled { get; private set; }
    public int WavesSpawned { get; private set; }
    public bool FinaleReady => Finished && FinaleElapsed >= FinaleDuration && WavesSpawned == FinaleWaveCount;
    public int AdvanceFinale(double seconds)
    {
        if (!Finished || !double.IsFinite(seconds) || seconds < 0) return 0;
        FinaleElapsed = Math.Min(FinaleDuration, FinaleElapsed + seconds);
        int due = Math.Min(FinaleWaveCount, 1 + (int)Math.Floor(FinaleElapsed * (FinaleWaveCount - 1) / FinaleDuration));
        int added = due - WavesScheduled;
        WavesScheduled = due;
        return added;
    }
    public void WaveSpawned() => WavesSpawned = Math.Min(WavesScheduled, WavesSpawned + 1);
    public bool DestinationCleared(Func<uint, bool> alive) => FinaleReady && !DestinationMonsters.Any(alive);
    public bool TryComplete(bool enemiesAlive) => FinaleReady && !enemiesAlive && Interlocked.CompareExchange(ref completed, 1, 0) == 0;
    public float Heading(double travelled)
    {
        int i = 1;
        while (i < points.Length - 1 && distances[i] <= travelled) i++;
        while (i > 1 && points[i] == points[i - 1]) i--;
        var a = points[i - 1]; var b = points[i];
        return Facing(b.X - a.X, b.Y - a.Y);
    }

    // DelveTractor rig.ast has an inverse-bind Y of -50.029 for front_axle, +8.061 for rear_axle.
    // Its nose faces +Y at zero, opposite the character's -Y; keep the front axle leading the displacement.
    public static float Facing(int dx, int dy) => MathF.Atan2(dx, -dy) + MathF.PI;

    // Separate stop/resume distances prevent repeated start/stop at the escort boundary.
    public static bool EscortMoving(bool moving, double separation, bool leading) => leading || separation < (moving ? 70 : 60);

    /// <summary>Nearest projection onto the rails: movement follows the rail, never a shortcut toward the player.</summary>
    public (double Along, double Away) Project((int X, int Y) position)
    {
        double best = double.PositiveInfinity, along = 0;
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1]; var b = points[i];
            double dx = b.X - a.X, dy = b.Y - a.Y, length2 = dx * dx + dy * dy;
            double t = length2 == 0 ? 0 : Math.Clamp(((position.X - a.X) * dx + (position.Y - a.Y) * dy) / length2, 0, 1);
            double distance = Math.Pow(position.X - a.X - t * dx, 2) + Math.Pow(position.Y - a.Y - t * dy, 2);
            double at = distances[i - 1] + t * Math.Sqrt(length2);
            if (distance > best || distance == best && at < along) continue;
            best = distance; along = at;
        }
        return (along, Math.Sqrt(best));
    }

    public double EscortStep((int X, int Y) player, double seconds, bool moving)
    {
        if (seconds <= 0) return 0;
        var projected = Project(player);
        bool leading = projected.Along > Travelled + 8 && projected.Away <= 70;
        double separation = DelveRailRoute.Distance(At(Travelled).At, player);
        if (!EscortMoving(moving, separation, leading)) return 0;
        double step = Math.Max(24 * seconds, leading ? projected.Along - Travelled - 10 : 0);
        // One native move per rail segment prevents a fast catch-up from cutting across corners/walls.
        double end = distances.FirstOrDefault(d => d > Travelled + .001, Length);
        double next = Math.Min(end, Travelled + step);
        if (!leading && DelveRailRoute.Distance(At(next).At, player) > 70)
        {
            double lo = Travelled, hi = next;
            for (int i = 0; i < 20; i++)
            {
                double middle = (lo + hi) / 2;
                if (DelveRailRoute.Distance(At(middle).At, player) <= 70) lo = middle; else hi = middle;
            }
            next = lo;
        }
        return Math.Max(0, next - Travelled);
    }
    public DelveJourney(IReadOnlyList<(int X, int Y)> route, int cost, int depth = 2)
    {
        if (route.Count < 2 || cost < 0 || cost > ushort.MaxValue) throw new ArgumentException("Invalid Delve journey");
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
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

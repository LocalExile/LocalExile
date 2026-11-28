/// <summary>A time-limited damage taken multiplier integrated over a continuous damage source's own interval.</summary>
public readonly record struct DamageTakenWindow(double Start, double End, double Multiplier)
{
    public double Seconds(double from, double until) => Math.Max(0, until - from)
        + (Multiplier - 1) * Math.Max(0, Math.Min(until, End) - Math.Max(from, Start));

    public double MovingSeconds(double from, double until, Func<double, double, double> moving)
    {
        if (until <= from) return 0;
        double moved = Math.Clamp(moving(from, until), 0, until - from);
        double start = Math.Max(from, Start), end = Math.Min(until, End);
        return end > start && moved > 0 && Multiplier != 1
            ? Math.Max(0, moved + (Multiplier - 1) * Math.Clamp(moving(start, end), 0, Math.Min(moved, end - start)))
            : moved;
    }
}

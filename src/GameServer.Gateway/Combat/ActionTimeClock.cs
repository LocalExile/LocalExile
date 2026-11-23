/// <summary>Elapsed action time at a piecewise constant rate. Settle the old rate at a publication boundary before
/// applying a new one; a zero rate suspends progress without losing the work remaining.</summary>
public sealed class ActionTimeClock(double startedAt, double rate)
{
    double settledAt = startedAt, elapsed;
    public double Rate { get; private set; } = Math.Max(0, rate);

    public double Elapsed(double now) => elapsed + Math.Max(0, now - settledAt) * Rate;

    public void SetRate(double now, double rate)
    {
        elapsed = Elapsed(now);
        settledAt = Math.Max(settledAt, now);
        Rate = Math.Max(0, rate);
    }

    public double Deadline(double target) => target <= elapsed ? settledAt
        : Rate > 0 ? settledAt + (target - elapsed) / Rate : double.PositiveInfinity;
}

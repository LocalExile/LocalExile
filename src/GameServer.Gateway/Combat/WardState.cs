/// <summary>Ward absorbs one hit, then restores in full after its delay. It never absorbs damage over time.
/// The 3.25 player-balance change set the base restoration delay to two seconds.</summary>
public sealed class WardState
{
    public uint Maximum { get; private set; }
    public uint Current => Broken ? 0 : Maximum;
    public bool Broken { get; private set; }
    public double RestoresAt { get; private set; } = double.PositiveInfinity;

    /// <summary>The maximum changed, or the ward broke or restored (the stat steps that read it run again).</summary>
    public Action? Changed;

    public void SetMaximum(uint maximum)
    {
        if (Maximum != maximum) { Maximum = maximum; Changed?.Invoke(); }
        if (maximum == 0) Restore();
    }

    public void Restore()
    {
        bool was = Broken;
        Broken = false; RestoresAt = double.PositiveInfinity;
        if (was) Changed?.Invoke();
    }
    public void RestoreCarried(bool broken, double remaining, double now)
    {
        Restore();
        if (Maximum > 0 && broken && remaining > 0) { Broken = true; RestoresAt = now + remaining; Changed?.Invoke(); }
    }

    public static double Delay(Func<uint, int> get) =>
        (get(Stat.BaseWardDelayOverride) is > 0 and var delay ? delay / 1000.0 : 2)
        / Math.Max(.01, 1 + get(Stat.WardDelayRecoveryPlusPct) / 100.0);

    public static uint Max(decimal flat, int increased) =>
        (uint)Math.Clamp(decimal.Truncate(Math.Max(0, flat) * Math.Max(0L, 100L + increased) / 100), 0, int.MaxValue);

    public bool Advance(double now)
    {
        if (!Broken || now < RestoresAt) return false;
        Restore();
        return true;
    }

    public int Damage(int amount, double now, Func<uint, int> get)
    {
        Advance(now);
        if (amount <= 0 || Broken || Maximum == 0) return Math.Max(0, amount);
        int bypass = (int)((long)amount * Math.Clamp(get(Stat.BaseDamageBypassWardPct), 0, 100) / 100);
        int absorbed = (int)Math.Min(Maximum, (uint)(amount - bypass));
        if (absorbed > 0 && get(Stat.WardDoesNotBreak) == 0)
        {
            Broken = true;
            RestoresAt = now + Delay(get);
            Changed?.Invoke();
        }
        return amount - absorbed;
    }
}

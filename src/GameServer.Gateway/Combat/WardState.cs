/// <summary>Ward absorbs one hit, then restores in full after its delay. It never absorbs damage over time.
/// The 3.25 player-balance change set the base restoration delay to two seconds.</summary>
sealed class WardState
{
    public uint Maximum { get; private set; }
    public uint Current => Broken ? 0 : Maximum;
    public bool Broken { get; private set; }
    public double RestoresAt { get; private set; } = double.PositiveInfinity;

    public void SetMaximum(uint maximum)
    {
        Maximum = maximum;
        if (maximum == 0) Restore();
    }

    public void Restore() { Broken = false; RestoresAt = double.PositiveInfinity; }
    public void RestoreCarried(bool broken, double remaining, double now)
    {
        Restore();
        if (Maximum > 0 && broken && remaining > 0) { Broken = true; RestoresAt = now + remaining; }
    }

    public static double Delay(Func<string, int> get) =>
        (get("base_ward_delay_override") is > 0 and var delay ? delay / 1000.0 : 2)
        / Math.Max(.01, 1 + get("ward_delay_recovery_+%") / 100.0);

    public static uint Max(decimal flat, int increased) =>
        (uint)Math.Clamp(decimal.Truncate(Math.Max(0, flat) * Math.Max(0L, 100L + increased) / 100), 0, int.MaxValue);

    public bool Advance(double now)
    {
        if (!Broken || now < RestoresAt) return false;
        Restore();
        return true;
    }

    public int Damage(int amount, double now, Func<string, int> get)
    {
        Advance(now);
        if (amount <= 0 || Broken || Maximum == 0) return Math.Max(0, amount);
        int bypass = (int)((long)amount * Math.Clamp(get("base_damage_bypass_ward_%"), 0, 100) / 100);
        int absorbed = (int)Math.Min(Maximum, (uint)(amount - bypass));
        if (absorbed > 0 && get("ward_does_not_break") == 0)
        {
            Broken = true;
            RestoresAt = now + Delay(get);
        }
        return amount - absorbed;
    }
}

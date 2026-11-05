/// <summary>
/// Soul Eater's eaten souls: one per enemy killed nearby, each giving 5% increased attack and cast speed and 1%
/// increased character size while the buff's source lasts. A soul is lost every half second once four seconds pass
/// without eating one (poedb's buff text); Path of Building's base maximum is 45.
/// </summary>
sealed class SoulEaterState
{
    public const int MaximumStacks = 45;
    public const double DecayIntervalSeconds = 0.5, RecentlySeconds = 4;

    int stacks;
    double lastSoul = double.NegativeInfinity;

    /// <summary>The buff's source is present (the stat and a flask effect): kept by the stat build, read by kills.</summary>
    public bool Enabled { get; set; }

    public int Stacks(double now)
    {
        Decay(now);
        return stacks;
    }

    /// <summary>Eats one soul, capped, and restarts the Recently window even at the cap.</summary>
    public int Soul(double now)
    {
        Decay(now);
        if (stacks < MaximumStacks) stacks++;
        lastSoul = now;
        return stacks;
    }

    public void Clear()
    {
        stacks = 0;
        lastSoul = double.NegativeInfinity;
        Enabled = false;
    }

    void Decay(double now)
    {
        if (!double.IsFinite(now) || now < 0) return;
        if (stacks == 0) { lastSoul = now; return; }
        double idle = now - lastSoul - RecentlySeconds;
        if (idle < 0) return;
        int lost = (int)Math.Floor(idle / DecayIntervalSeconds);
        if (lost <= 0) return;
        stacks = Math.Max(0, stacks - lost);
        lastSoul = stacks > 0 ? lastSoul + lost * DecayIntervalSeconds : now;
    }
}

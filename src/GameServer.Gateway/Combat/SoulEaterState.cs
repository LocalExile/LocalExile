/// <summary>
/// Soul Eater's eaten souls: one per enemy killed nearby, each giving 5% increased attack and cast speed and 1%
/// increased character size while the buff's source lasts. A soul is lost every half second once four seconds pass
/// without eating one (poedb's buff text); Path of Building's base maximum is 45.
/// </summary>
public sealed class SoulEaterState
{
    public const int MaximumStacks = 45;
    public const double DecayIntervalSeconds = 0.5, RecentlySeconds = 4;

    int stacks;
    double lastSoul = double.NegativeInfinity;

    /// <summary>The buff's source is present (the stat and a flask effect): kept by the stat build, read by kills.</summary>
    public bool Enabled { get; set; }

    public int Limit { get; private set; } = MaximumStacks;

    public void SetMaximum(int maximum)
    {
        Limit = Math.Clamp(maximum, 0, ushort.MaxValue);
        stacks = Math.Min(stacks, Limit);
    }

    /// <summary>The buff instance the client has, and the count it was last told.</summary>
    public ushort Buff { get; set; }
    public int SentStacks { get; set; }
    public bool SentUsingFlask { get; set; }
    /// <summary>The "soul_eater_aura" instance (277): the EPK that draws the soul visuals, separate from the icon.</summary>
    public ushort AuraBuff { get; set; }
    /// <summary>The souls eaten since the last presentation (the dead object's id and its cell): the player tick sends
    /// one beam per soul.</summary>
    public List<(uint Id, int X, int Y)> Souls { get; } = [];

    public int Stacks(double now)
    {
        Decay(now);
        return stacks;
    }

    /// <summary>Eats one soul, capped, and restarts the Recently window even at the cap.</summary>
    public int Soul(double now)
    {
        Decay(now);
        if (stacks < Limit) stacks++;
        lastSoul = now;
        return stacks;
    }

    public void Clear()
    {
        stacks = 0;
        lastSoul = double.NegativeInfinity;
        Enabled = false;
        Souls.Clear();
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

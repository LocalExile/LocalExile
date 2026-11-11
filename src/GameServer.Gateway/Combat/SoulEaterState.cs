/// <summary>
/// Soul Eater's eaten souls: one per enemy killed nearby (Soulthirst: "Gain Soul Eater during any Flask Effect") or per
/// hit on a rare or unique enemy (Soul Ascension), each giving 5% increased attack and cast speed and 1% increased
/// character size while the buff's source lasts. A soul is lost every half second once four seconds pass without
/// eating one (poedb's buff text); Path of Building's base maximum is 45.
/// </summary>
public sealed class SoulEaterState
{
    public const int MaximumStacks = 45;
    public const double DecayIntervalSeconds = 0.5, RecentlySeconds = 4, AscensionCooldownSeconds = 0.5;

    int stacks;
    double lastSoul = double.NegativeInfinity, lastHitSoul = double.NegativeInfinity;

    /// <summary>The buff's source is present (either source below): kept by the stat build, read by kills and hits.</summary>
    public bool Enabled { get; set; }

    /// <summary>"Gain Soul Eater during any Flask Effect" (Soulthirst): kills within the radius feed a soul.</summary>
    public bool FlaskFed { get; set; }

    /// <summary>Soul Ascension's "Eat a Soul when you Hit a Rare or Unique Enemy": hits feed a soul.</summary>
    public bool HitFed { get; set; }

    /// <summary>The hit source's cooldown, from the item's stat (500 ms); 0 when the source is absent.</summary>
    public double HitCooldownSeconds { get; set; } = AscensionCooldownSeconds;

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

    /// <summary>Soul Ascension's hit source: one soul per hit on a rare or unique enemy, no more than once per the
    /// item's cooldown (500 ms). The cooldown runs even at the cap, as the item's "no more than once" reads.</summary>
    public bool TryHitSoul(double now)
    {
        if (!HitFed) return false;
        if (double.IsFinite(now) && now - lastHitSoul < Math.Max(0, HitCooldownSeconds)) return false;
        lastHitSoul = now;
        Soul(now);
        return true;
    }

    public void Clear()
    {
        stacks = 0;
        lastSoul = double.NegativeInfinity;
        lastHitSoul = double.NegativeInfinity;
        Enabled = false;
        FlaskFed = false;
        HitFed = false;
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

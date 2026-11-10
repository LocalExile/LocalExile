/// <summary>Arcane Surge Support's buff and per-skill mana-spent counters, and Inspiration's charges. Guarded by the
/// connection's Monsters lock.</summary>
public sealed class SupportGemState
{
    /// <summary>When the Arcane Surge buff ends (Clock seconds); 0 means it is not running.</summary>
    public double ArcaneSurgeUntil;

    /// <summary>The support's cast speed and mana regeneration values captured when the buff started.</summary>
    public int ArcaneSurgeCastSpeedPct;
    public int ArcaneSurgeManaRegenPct;

    /// <summary>Mana spent on each supported spell since its last Arcane Surge trigger.</summary>
    public readonly Dictionary<ushort, long> ArcaneSurgeSpent = [];

    /// <summary>Inspiration charges held (0-5), when they expire, and the shared mana counter towards the support's
    /// reset threshold.</summary>
    public int InspirationCharges;
    public double InspirationUntil;
    public long InspirationSpent;

    /// <summary>Client instances for the two buffs and the counts last shown for Inspiration.</summary>
    public ushort ArcaneSurgeBuff, PresentedArcaneSurge, InspirationBuff, PresentedInspiration;
    public int PresentedInspirationCharges;
    public bool ArcaneSurgeRefresh, InspirationRefresh, PendingStats;

    public bool ArcaneSurgeActiveAt(double now) => ArcaneSurgeUntil > now;
}

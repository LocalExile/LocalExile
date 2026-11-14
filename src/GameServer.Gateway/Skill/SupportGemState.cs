/// <summary>Arcane Surge Support's buff and per-skill mana-spent counters, Inspiration's charges, and Cruelty's per-skill
/// buff. Guarded by the connection's Monsters lock.</summary>
public sealed class SupportGemState
{
    /// <summary>When the Arcane Surge buff ends (Clock seconds); 0 means it is not running.</summary>
    public double ArcaneSurgeUntil;

    /// <summary>The support's cast speed and mana regeneration values captured when the buff started.</summary>
    public int ArcaneSurgeCastSpeedPct;
    public int ArcaneSurgeManaRegenPct;

    /// <summary>The deadline the client's Arcane Surge buff was last shown with.</summary>
    public double ArcaneSurgeShownUntil;

    /// <summary>Mana spent on each supported spell since its last Arcane Surge trigger.</summary>
    public readonly Dictionary<ushort, long> ArcaneSurgeSpent = [];

    /// <summary>Inspiration charges held (0-5), when they expire, and the shared mana counter towards the support's
    /// reset threshold.</summary>
    public int InspirationCharges;
    public double InspirationUntil;
    public long InspirationSpent;

    /// <summary>When each Cruelty-supported skill's buff ends (Clock seconds; absent means not running) and the more
    /// damage over time that hit granted it (0-40 percent). The client shows the strongest running one.</summary>
    public readonly Dictionary<ushort, double> CrueltyUntil = [];
    public readonly Dictionary<ushort, int> CrueltyPct = [];

    /// <summary>The one Cruelty buff instance shown to the client, the value and deadline it was last shown with, and
    /// whether the last gain or refresh changed something the client must be told about.</summary>
    public ushort CrueltyBuff, PresentedCruelty;
    public int PresentedCrueltyPct;
    public double CrueltyShownUntil;
    public bool CrueltyRefresh;

    /// <summary>Client instances for the two buffs and the counts last shown for Inspiration.</summary>
    public ushort ArcaneSurgeBuff, PresentedArcaneSurge, InspirationBuff, PresentedInspiration;
    public int PresentedInspirationCharges;
    public bool ArcaneSurgeRefresh, InspirationRefresh, PendingStats;

    public bool ArcaneSurgeActiveAt(double now) => ArcaneSurgeUntil > now;

    public int CrueltyActiveAt(ushort skillId, double now) =>
        CrueltyUntil.GetValueOrDefault(skillId) > now ? CrueltyPct.GetValueOrDefault(skillId) : 0;
}

using System.Runtime.CompilerServices;

/// <summary>Untiring: "Gain 1.5% of Physical Damage prevented from Hits in the past 10 seconds as Life Regeneration per
/// second" (stat value 15 = 1.5%, BuffDefinitions juggernaut_phys_damage_prevented_as_life_regen shows no stat). Each hit
/// pays for its own 10 s. Prevented is the raw hit minus what it finally dealt: armour, reduction, Fortification, what a
/// block keeps out, avoidance and immunity count; evasion, ward, energy shield and damage taken as another type do not
/// (wiki Prevented damage). The recovery writer calls
/// <see cref="Take"/> with its own step, so scheduling delays change the step, never the rate. Owned by the area's frame.
/// shortcut: an area change leaves the past 10 s behind, as Life Recoup does, until a capture shows the regeneration
/// carries over.</summary>
public sealed class PreventedDamageRecovery
{
    const uint RegenStat = global::Stat.GainPctTotalPhysDamagePreventedInThePast10SecAsLifeRegenPerSec;
    const double WindowSeconds = 10;

    readonly Queue<(double At, double Amount)> hits = new();

    /// <summary>The share of the window's damage regenerated per second: a regeneration, so its increases and
    /// prohibitions apply (PoB's pseudo recoup scales it by them).</summary>
    public static double PerSecond(Func<uint, int> get) =>
        get(RegenStat) / 1000.0 * (PanelStats.ZealotsOath(get)
            ? (PanelStats.LifeRegenerationBlocked(get) || !EnergyShield.ConversionAllowed(get) ? 0 : EnergyShield.RegenerationMultiplier(get))
            : PanelStats.LifeRegenerationMultiplier(get));

    public void Add(double now, double amount)
    {
        if (amount > 0) hits.Enqueue((now, amount));
    }

    /// <summary>The Life regenerated between <paramref name="from"/> and <paramref name="now"/>.</summary>
    public double Take(double from, double now, double perSecond)
    {
        double total = 0;
        foreach (var (at, amount) in hits)
            total += amount * Math.Max(0, Math.Min(now, at + WindowSeconds) - Math.Max(from, at));
        while (hits.TryPeek(out var oldest) && oldest.At + WindowSeconds <= now) hits.Dequeue();
        return total * perSecond;
    }

    public void Clear() => hits.Clear();
}

public static partial class Game
{
    static readonly Store<Player, PreventedDamageRecovery> PreventedDamage = new();

    public static PreventedDamageRecovery PreventedDamageOf(Player player) => PreventedDamage.GetValue(player, _ => new());
}

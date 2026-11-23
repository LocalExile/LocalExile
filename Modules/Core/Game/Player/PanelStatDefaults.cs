

/// <summary>The inherent character values from Metadata/Characters/Character.ot needed by the Brand, special charge and
/// critical ailment panel rows. Add them to original actor sources once, before derivation; final developer replacements remain
/// the caller's responsibility. These are coefficients and a Brand limit, not charge counts or gameplay effects.</summary>
public static class PanelStatDefaults
{
    public static readonly (uint Stat, int Value)[] Inherent =
    [
        (Stat.BaseTotalNumberOfSigilsAllowed, 3),
        (Stat.ChanceToDealTripleDamagePctPerBrutalCharge, 3),
        (Stat.StunThresholdPlusPctPerBrutalCharge, 10),
        (Stat.AilmentDamagePlusPctFinalPerAfflictionCharge, 8),
        (Stat.NonDamagingAilmentEffectPlusPctFinalPerAfflictionCharge, 8),
        (Stat.ElementalDamageTakenGoesToEnergyShieldOver4SecondsPctPerAbsorptionCharge, 12),
        // Damaging ailments from critical strikes: +50% to their damage over time multiplier (the wiki's Ignite, Bleeding
        // and Poison), in the panel's critical rows and so in every ailment read from them.
        (Stat.CriticalAilmentDotMultiplierPlus, 50),
    ];

    public static List<(uint Stat, int Value)> Compute(Func<uint, int> raw) =>
        [.. Inherent.Select(s => (s.Stat, (int)Math.Clamp((long)raw(s.Stat) + s.Value, int.MinValue, int.MaxValue)))];

    /// <summary>The inherent value of one stat (0 for any other): what <see cref="Compute"/> adds to its raw sources.
    /// The charge expansion (WithCharges) reads the special per-charge coefficients through this before the defaults
    /// enter the published sources.</summary>
    public static int Default(uint stat) => Defaults.GetValueOrDefault(stat);

    // Default asks for every per-charge stat of every snapshot: one lookup instead of a scan (the first entry of a name wins).
    static readonly System.Collections.Frozen.FrozenDictionary<uint, int> Defaults =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(Inherent.DistinctBy(s => s.Stat), s => s.Stat, s => s.Value);
}



/// <summary>The six inherent character values from Metadata/Characters/Character.ot needed by the Brand and special
/// charge panel rows. Add them to original actor sources once, before derivation; final developer replacements remain
/// the caller's responsibility. These are coefficients and a Brand limit, not charge counts or gameplay effects.</summary>
static class PanelStatDefaults
{
    public static readonly (string Stat, int Value)[] Inherent =
    [
        ("base_total_number_of_sigils_allowed", 3),
        ("chance_to_deal_triple_damage_%_per_brutal_charge", 3),
        ("stun_threshold_+%_per_brutal_charge", 10),
        ("ailment_damage_+%_final_per_affliction_charge", 8),
        ("non_damaging_ailment_effect_+%_final_per_affliction_charge", 8),
        ("elemental_damage_taken_goes_to_energy_shield_over_4_seconds_%_per_absorption_charge", 12),
    ];

    public static List<(string Stat, int Value)> Compute(Func<string, int> raw) =>
        [.. Inherent.Select(s => (s.Stat, (int)Math.Clamp((long)raw(s.Stat) + s.Value, int.MinValue, int.MaxValue)))];

    /// <summary>The inherent value of one stat (0 for any other): what <see cref="Compute"/> adds to its raw sources.
    /// The charge expansion (WithCharges) reads the special per-charge coefficients through this before the defaults
    /// enter the published sources.</summary>
    public static int Default(string stat) => Inherent.FirstOrDefault(s => s.Stat == stat).Value;
}

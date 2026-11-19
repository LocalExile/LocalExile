/// <summary>
/// Per-charge stats of passives and items beyond <see cref="ChargeEffects.PerCharge"/>, each into the stat the server
/// already calculates with (stat descriptions for the wording; the targets are the plain stats of the same wording):
/// the "reduced Extra Damage from Critical Strikes per Endurance Charge" implicit;
/// Assassin's Deathmarked "per maximum Power Charge" Mark effect and instant leech. The "while at maximum … Charges"
/// item stats are StatConditions' (ItemConditionFlags), not here.
/// </summary>
public static class ChargeScaling
{
    static ChargeEffects.Rule E(string stat, params string[] targets) => new(stat, ChargeType.Endurance, targets);
    static ChargeEffects.Rule F(string stat, params string[] targets) => new(stat, ChargeType.Frenzy, targets);
    static ChargeEffects.Rule P(string stat, params string[] targets) => new(stat, ChargeType.Power, targets);

    public static readonly IReadOnlyList<ChargeEffects.Rule> Rules =
    [
        E("self_critical_strike_multiplier_-%_per_endurance_charge", "base_self_critical_strike_multiplier_-%"),
        new("mark_skills_curse_effect_+%_per_maximum_power_charge", ChargeType.Power, ["mark_skills_curse_effect_+%"], PerMaximum: true),
        new("leech_%_is_instant_per_maximum_power_charge", ChargeType.Power, ["leech_%_is_instant"], PerMaximum: true),
    ];

    /// <summary>"Count as having maximum number of … Charges" (Ralakesh's Impatience): the per-charge effects and the
    /// counts shown use the maximum.</summary>
    public static bool CountsAsMaximum(ChargeType type, Func<string, int> get) =>
        get(CountAsMaximumStat(type)) != 0 || get(AllCountAsMaximum) != 0;

    /// <summary>The two stats <see cref="CountsAsMaximum"/> reads, for a caller on the stat-snapshot path that checks them
    /// with its own lookup instead of making a delegate per type.</summary>
    public static string CountAsMaximumStat(ChargeType type) => CountAsMaximumStats[(int)type];
    public const string AllCountAsMaximum = "count_as_having_max_endurance_frenzy_power_charges";

    // Built once: the condition flags ask on every item-stat lookup (performance.md, "Hot paths").
    static readonly string[] CountAsMaximumStats = [.. Enum.GetValues<ChargeType>()
        .OrderBy(t => (int)t).Select(t => $"count_as_having_max_{Charges.Name(t)}_charges")];
}

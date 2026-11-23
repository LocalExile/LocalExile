using GameServer.Core;

/// <summary>
/// Per-charge stats of passives and items beyond <see cref="ChargeEffects.PerCharge"/>, each into the stat the server
/// already calculates with (stat descriptions for the wording; the targets are the plain stats of the same wording):
/// the "reduced Extra Damage from Critical Strikes per Endurance Charge" implicit;
/// Assassin's Deathmarked "per maximum Power Charge" Mark effect and instant leech. The "while at maximum … Charges"
/// item stats are StatConditions' (ItemConditionFlags), not here.
/// </summary>
public static class ChargeScaling
{
    static ChargeEffects.Rule E(uint stat, params uint[] targets) => new(stat, ChargeType.Endurance, targets);
    static ChargeEffects.Rule F(uint stat, params uint[] targets) => new(stat, ChargeType.Frenzy, targets);
    static ChargeEffects.Rule P(uint stat, params uint[] targets) => new(stat, ChargeType.Power, targets);

    public static readonly IReadOnlyList<ChargeEffects.Rule> Rules =
    [
        E(Stat.SelfCriticalStrikeMultiplierMinusPctPerEnduranceCharge, Stat.BaseSelfCriticalStrikeMultiplierMinusPct),
        new(Stat.MarkSkillsCurseEffectPlusPctPerMaximumPowerCharge, ChargeType.Power, [Stat.MarkSkillsCurseEffectPlusPct], PerMaximum: true),
        new(Stat.LeechPctIsInstantPerMaximumPowerCharge, ChargeType.Power, [Stat.LeechPctIsInstant], PerMaximum: true),
    ];

    /// <summary>"Count as having maximum number of … Charges" (Ralakesh's Impatience): the per-charge effects and the
    /// counts shown use the maximum.</summary>
    public static bool CountsAsMaximum(ChargeType type, Func<uint, int> get) =>
        get(CountAsMaximumStat(type)) != 0 || get(AllCountAsMaximum) != 0;

    /// <summary>The two stats <see cref="CountsAsMaximum"/> reads, for a caller on the stat-snapshot path that checks them
    /// with its own lookup instead of making a delegate per type.</summary>
    public static uint CountAsMaximumStat(ChargeType type) => CountAsMaximumStats[(int)type];
    public const uint AllCountAsMaximum = Stat.CountAsHavingMaxEnduranceFrenzyPowerCharges;

    // Built once: the condition flags ask on every item-stat lookup (performance.md, "Hot paths").
    static readonly uint[] CountAsMaximumStats = [.. Enum.GetValues<ChargeType>()
        .OrderBy(t => (int)t).Select(t => Stat.Of($"count_as_having_max_{Charges.Name(t)}_charges"))];
}

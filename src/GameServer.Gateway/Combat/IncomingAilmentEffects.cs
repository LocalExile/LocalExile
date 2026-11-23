/// <summary>
/// Self-poison coefficients read from the same live poison count as the incoming ailment owner.
/// These are final-stat expansions, not independent state.
/// </summary>
public static class IncomingAilmentEffects
{
    public sealed record Rule(uint Stat, uint Target, int? Cap = null);

    public static readonly IReadOnlyList<Rule> PerPoison =
    [
        new(Stat.ChaosDamageResistancePctPerPoisonStack, Stat.BaseChaosDamageResistancePct),
        new(Stat.DamagePlusPctPerPoisonUpTo75Pct, Stat.DamagePlusPct, 75),
        new(Stat.MovementSpeedPlusPctPerPoisonUpTo50Pct, Stat.BaseMovementVelocityPlusPct, 50),
        new(Stat.EnergyShieldRegenerationRatePerMinutePerPoisonStack,
            Stat.BaseEnergyShieldRegenerationRatePerMinute),
    ];

    static readonly HashSet<uint> Expanded = [.. PerPoison.Select(r => r.Stat)];

    public static bool Expands(uint stat) => Expanded.Contains(stat);

    public static Dictionary<uint, int> Expand(Func<uint, int> get, int stacks)
    {
        var added = new Dictionary<uint, int>();
        if (stacks <= 0) return added;
        foreach (var rule in PerPoison)
        {
            long value = (long)get(rule.Stat) * stacks;
            if (rule.Cap is int cap) value = Math.Min(value, cap);
            if (value != 0)
                added[rule.Target] = (int)Math.Clamp(added.GetValueOrDefault(rule.Target) + value,
                    int.MinValue, int.MaxValue);
        }
        return added;
    }
}

/// <summary>
/// Self-poison coefficients read from the same live poison count as the incoming ailment owner.
/// These are final-stat expansions, not independent state.
/// </summary>
static class IncomingAilmentEffects
{
    public sealed record Rule(string Stat, string Target, int? Cap = null);

    public static readonly IReadOnlyList<Rule> PerPoison =
    [
        new("chaos_damage_resistance_%_per_poison_stack", "base_chaos_damage_resistance_%"),
        new("damage_+%_per_poison_up_to_75%", "damage_+%", 75),
        new("movement_speed_+%_per_poison_up_to_50%", "base_movement_velocity_+%", 50),
        new("energy_shield_regeneration_rate_per_minute_per_poison_stack",
            "base_energy_shield_regeneration_rate_per_minute"),
    ];

    static readonly HashSet<string> Expanded = [.. PerPoison.Select(r => r.Stat)];

    public static bool Expands(string stat) => Expanded.Contains(stat);

    public static Dictionary<string, int> Expand(Func<string, int> get, int stacks)
    {
        var added = new Dictionary<string, int>(StringComparer.Ordinal);
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

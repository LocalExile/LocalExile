/// <summary>Item coefficients use completed full maximum pools, independently of spending and reservation.</summary>
public static class MaximumPoolItemSources
{
    public const uint SpellCriticalPerLife = Stat.SpellCriticalStrikeChancePlusPctPer100MaxLife;
    static readonly (uint Input, uint Output, bool LowestPool)[] Rules =
    [
        (Stat.SpellDamagePlusPctPer100MaxLife, Stat.SpellDamagePlusPct, false),
        (SpellCriticalPerLife, Stat.SpellCriticalStrikeChancePlusPct, false),
        (Stat.DamageOverTimePlusPctPer100MaxLife, Stat.DamageOverTimePlusPct, false),
        (Stat.MinimumAddedFireDamagePer100LowestOfMaxLifeMana, Stat.GlobalMinimumAddedFireDamage, true),
        (Stat.MaximumAddedFireDamagePer100LowestOfMaxLifeMana, Stat.GlobalMaximumAddedFireDamage, true),
    ];

    public static long Amount(int coefficient, uint maximum) => (long)coefficient * (maximum / 100);

    public static void Apply(StatMap sources, StatCatalog stats, uint maximumLife, uint maximumMana) =>
        Apply(sources, stats, maximumLife, maximumMana, 0);

    public static void Apply(StatMap sources, StatCatalog stats, uint maximumLife, uint maximumMana, uint maximumEnergyShield)
    {
        foreach (var (input, output, lowest) in Rules)
            if (input is var key && sources.GetValueOrDefault(key) is int coefficient and not 0
                && output is var target)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target)
                    + Amount(coefficient, lowest ? Math.Min(maximumLife, maximumMana) : maximumLife));
        int shieldRegen = sources.GetValueOrDefault(Stat.LifeRegenerationRatePerMinutePctPer500MaximumEnergyShield);
        if (shieldRegen != 0 && maximumEnergyShield >= 500)
            sources[Stat.LifeRegenerationRatePerMinutePct] = ActorScale.Saturate(
                (long)sources.GetValueOrDefault(Stat.LifeRegenerationRatePerMinutePct) + (long)shieldRegen * (maximumEnergyShield / 500));
    }
}

public static class LevelItemSources
{
    static readonly (uint Input, uint Output)[] Rules =
    [
        (Stat.ChaosDamagePlusPctPerLevel, Stat.ChaosDamagePlusPct),
        (Stat.CriticalStrikeChancePlusPctPerLevel, Stat.CriticalStrikeChancePlusPct),
        (Stat.EnergyShieldPerLevel, Stat.BaseMaximumEnergyShield),
        (Stat.LifeRegenerationRatePerMinutePerLevel, Stat.BaseLifeRegenerationRatePerMinute),
    ];

    // The level is part of the qualification/cache key, including explicit character-build previews.
    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats, int level)
    {
        foreach (var (input, output) in Rules)
            if (input is var coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && output is var target)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target) + (long)value * Math.Max(0, level));
    }
}

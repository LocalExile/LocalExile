public static class ItemChargeCosts
{
    public const uint ManaEfficiencyPerEndurance = Stat.BaseManaCostEfficiencyPlusPctPerEnduranceCharge;
    public const uint ManaEfficiency = Stat.BaseManaCostEfficiencyPlusPct;

    public static IEnumerable<(uint Stat, int Value)> Expand(Func<uint, int> get,
        Func<ChargeType, int> held, Func<ChargeType, bool> converted)
    {
        int count = converted(ChargeType.Endurance) ? 0 : Math.Max(0, held(ChargeType.Endurance));
        int amount = ActorScale.Saturate((long)get(ManaEfficiencyPerEndurance) * count);
        if (amount != 0) yield return (ManaEfficiency, amount);
    }
}

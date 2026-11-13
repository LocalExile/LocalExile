public static class ItemChargeCosts
{
    public const string ManaEfficiencyPerEndurance = "base_mana_cost_efficiency_+%_per_endurance_charge";
    public const string ManaEfficiency = "base_mana_cost_efficiency_+%";

    public static IEnumerable<(string Stat, int Value)> Expand(Func<string, int> get,
        Func<ChargeType, int> held, Func<ChargeType, bool> converted)
    {
        int count = converted(ChargeType.Endurance) ? 0 : Math.Max(0, held(ChargeType.Endurance));
        int amount = ActorScale.Saturate((long)get(ManaEfficiencyPerEndurance) * count);
        if (amount != 0) yield return (ManaEfficiency, amount);
    }
}

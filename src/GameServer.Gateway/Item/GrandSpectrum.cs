using GameServer.Core.Protocol.Instance;

public static class GrandSpectrum
{
    const uint CountStat = Stat.NumberOfStackableUniqueJewels;

    // The current three outcomes for each base. The old mana and armour versions still have readable item mods.
    static readonly Dictionary<string, string[]> Variants = new()
    {
        ["Crimson Jewel"] = ["AllResistancePerStackableJewelUnique__1", "MaximumLifePerStackableJewelUnique__1",
            "MinimumEnduranceChargesPerStackableJewelUnique__1"],
        ["Cobalt Jewel"] = ["CriticalStrikeChancePerStackableJewelUnique__1", "MinionCriticalStrikeMultiplierPerStackableJewelUnique__1",
            "MinimumPowerChargesPerStackableJewelUnique__1"],
        ["Viridian Jewel"] = ["IncreasedDamagePerStackableJewelUnique__1", "AvoidElementalAilmentsPerStackableJewelUnique__1",
            "MinimumFrenzyChargesPerStackableJewelUnique__1"],
    };

    // Each "<prefix>_per_stackable_unique_jewel" coefficient and the stat it adds to.
    static readonly (uint Per, uint Target)[] Targets = [.. new (string Prefix, uint Target)[]
    {
        ("X_mana", Stat.BaseMaximumMana),
        ("X_armour", Stat.BasePhysicalDamageReductionRating),
        ("elemental_damage_+%", Stat.ElementalDamagePlusPct),
        ("critical_strike_chance_+%", Stat.CriticalStrikeChancePlusPct),
        ("elemental_resistance_%", Stat.BaseResistAllElementsPct),
        ("maximum_life_+%", Stat.MaximumLifePlusPct),
        ("avoid_all_elemental_status_%", Stat.AvoidAllElementalStatusPct),
        ("minion_critical_strike_multiplier_+", Stat.MinionCriticalStrikeMultiplierPlus),
        ("minimum_endurance_charges", Stat.BaseMinimumEnduranceCharges),
        ("minimum_frenzy_charges", Stat.BaseMinimumFrenzyCharges),
        ("minimum_power_charges", Stat.BaseMinimumPowerCharges),
    }.Select(t => (Stat.Of(t.Prefix + "_per_stackable_unique_jewel"), t.Target))];

    public static string? Roll(string baseName, Random random) =>
        Variants.TryGetValue(baseName, out var variants) ? variants[random.Next(variants.Length)] : null;

    public static bool IsSpectrum(Item item, ModCatalog mods) => item.Rarity == ItemRarity.Unique
        && ItemModStats.Values(item, mods).Any(stat => stat.Id == CountStat && stat.Value > 0);

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats)
    {
        if (!sources.TryGetValue(CountStat, out int count) || count <= 0) return;
        foreach (var (per, output) in Targets)
            if (sources.TryGetValue(per, out int coefficient))
                sources[output] = (int)Math.Clamp(sources.GetValueOrDefault(output) + (long)coefficient * count, int.MinValue, int.MaxValue);
    }
}

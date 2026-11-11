using GameServer.Core.Protocol.Instance;

public static class GrandSpectrum
{
    const string CountStat = "number_of_stackable_unique_jewels";

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

    static readonly Dictionary<string, string> Targets = new()
    {
        ["X_mana"] = "base_maximum_mana", ["X_armour"] = "base_physical_damage_reduction_rating",
        ["elemental_damage_+%"] = "elemental_damage_+%", ["critical_strike_chance_+%"] = "critical_strike_chance_+%",
        ["elemental_resistance_%"] = "base_resist_all_elements_%", ["maximum_life_+%"] = "maximum_life_+%",
        ["avoid_all_elemental_status_%"] = "avoid_all_elemental_status_%",
        ["minion_critical_strike_multiplier_+"] = "minion_critical_strike_multiplier_+",
        ["minimum_endurance_charges"] = "base_minimum_endurance_charges",
        ["minimum_frenzy_charges"] = "base_minimum_frenzy_charges",
        ["minimum_power_charges"] = "base_minimum_power_charges",
    };

    public static string? Roll(string baseName, Random random) =>
        Variants.TryGetValue(baseName, out var variants) ? variants[random.Next(variants.Length)] : null;

    public static bool IsSpectrum(Item item, ModCatalog mods) => item.Rarity == ItemRarity.Unique
        && ItemModStats.Values(item, mods).Any(stat => stat.Id == CountStat && stat.Value > 0);

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats)
    {
        if (stats.Key(CountStat) is not uint key || !sources.TryGetValue(key, out int count) || count <= 0) return;
        foreach (var (prefix, target) in Targets)
            if (stats.Key(prefix + "_per_stackable_unique_jewel") is uint per
                && sources.TryGetValue(per, out int coefficient) && stats.Key(target) is uint output)
                sources[output] = (int)Math.Clamp(sources.GetValueOrDefault(output) + (long)coefficient * count, int.MinValue, int.MaxValue);
    }
}

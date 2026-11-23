using GameServer.Core.Protocol.Instance;


/// <summary>The native item-tooltip defences, before character-wide modifiers and slot conversions.</summary>
public static class ItemDefences
{
    public static (int Armour, int Evasion, int EnergyShield) Calculate(Item item, ItemCatalog.BaseDefences defences,
        ModCatalog mods, byte? baseRoll = null) =>
        CalculateLocal(defences.AtRoll(baseRoll ?? item.DefenceBaseRoll), ItemModStats.Local(item, mods), item.Quality);

    public static int Ward(Item item, ItemCatalog.BaseDefences defences, ModCatalog mods, byte? baseRoll = null)
    {
        int roll = baseRoll ?? item.DefenceBaseRoll;
        if (roll > Item.SpecialDefenceBaseRoll) throw new ArgumentOutOfRangeException(nameof(baseRoll));
        if (roll == Item.SpecialDefenceBaseRoll) roll = 0;
        long basis = defences.WardMin + (((long)defences.WardMax - defences.WardMin) * roll + 15) / 31;
        var local = ItemModStats.Local(item, mods);
        long quality = local.GetValueOrDefault(Stat.LocalQualityDoesNotIncreaseDefences) != 0 ? 0
            : Math.Max(0L, (long)item.Quality + local.GetValueOrDefault(Stat.LocalItemQualityPlus));
        float factor = Math.Max(0f, 1f + local.GetValueOrDefault(Stat.LocalWardPlusPct) * 0.01f) * (1f + quality * 0.01f);
        float value = Math.Max(0L, basis + local.GetValueOrDefault(Stat.LocalWard)) * factor + 0.5f;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }

    /// <summary>Local defences from an already resolved modifier map, using the minimum base roll.</summary>
    public static (int Armour, int Evasion, int EnergyShield) Of(
        ItemCatalog.BaseDefences baseDefences, IReadOnlyDictionary<uint, int> local, int quality) =>
        CalculateLocal(baseDefences.AtRoll(0), local, quality);

    static (int Armour, int Evasion, int EnergyShield) CalculateLocal(
        (int Armour, int Evasion, int EnergyShield) basis, IReadOnlyDictionary<uint, int> local, int itemQuality)
    {
        long Get(uint name) => local.GetValueOrDefault(name);
        long quality = Get(Stat.LocalQualityDoesNotIncreaseDefences) != 0 ? 0
            : Math.Max(0L, (long)itemQuality + Get(Stat.LocalItemQualityPlus));
        int Rating(int basis, long added, long increased)
        {
            // Native getters multiply the two single-precision factors first, then the base, add 0.5f
            // and truncate. Decimal rounding changes real tooltip results (e.g. 150 * 2.37f -> 355).
            float factor = Math.Max(0f, 1f + increased * 0.01f) * (1f + quality * 0.01f);
            float value = Math.Max(0L, basis + added) * factor + 0.5f;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
        long shared = Get(Stat.LocalArmourAndEvasionPlusPct) + Get(Stat.LocalArmourAndEvasionAndEnergyShieldPlusPct);
        int armour = Rating(basis.Armour, Get(Stat.LocalBasePhysicalDamageReductionRating),
            Get(Stat.LocalPhysicalDamageReductionRatingPlusPct) + Get(Stat.LocalArmourAndEnergyShieldPlusPct) + shared);
        int evasion = Rating(basis.Evasion, Get(Stat.LocalBaseEvasionRating) + Get(Stat.LocalEvasionRatingAndEnergyShield),
            Get(Stat.LocalEvasionRatingPlusPct) + Get(Stat.LocalEvasionAndEnergyShieldPlusPct) + shared);
        // Native 3.29.3c ES getter 0x1424b5f00: either prohibition wins over base, flat mods, increases and quality.
        int energyShield = Get(Stat.LocalNoEnergyShield) != 0
            || Get(Stat.LocalUniqueTabulaRasaNoRequirementOrEnergyShield) != 0 ? 0
            : Rating(basis.EnergyShield, Get(Stat.LocalEnergyShield) + Get(Stat.LocalEvasionRatingAndEnergyShield),
                Get(Stat.LocalEnergyShieldPlusPct) + Get(Stat.LocalArmourAndEnergyShieldPlusPct)
                + Get(Stat.LocalEvasionAndEnergyShieldPlusPct) + Get(Stat.LocalArmourAndEvasionAndEnergyShieldPlusPct));
        return (armour, evasion, energyShield);
    }
}

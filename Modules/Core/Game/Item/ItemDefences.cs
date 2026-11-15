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
        long quality = local.GetValueOrDefault("local_quality_does_not_increase_defences") != 0 ? 0
            : Math.Max(0L, (long)item.Quality + local.GetValueOrDefault("local_item_quality_+"));
        float factor = Math.Max(0f, 1f + local.GetValueOrDefault("local_ward_+%") * 0.01f) * (1f + quality * 0.01f);
        float value = Math.Max(0L, basis + local.GetValueOrDefault("local_ward")) * factor + 0.5f;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }

    /// <summary>Local defences from an already resolved modifier map, using the minimum base roll.</summary>
    public static (int Armour, int Evasion, int EnergyShield) Of(
        ItemCatalog.BaseDefences baseDefences, IReadOnlyDictionary<string, int> local, int quality) =>
        CalculateLocal(baseDefences.AtRoll(0), local, quality);

    static (int Armour, int Evasion, int EnergyShield) CalculateLocal(
        (int Armour, int Evasion, int EnergyShield) basis, IReadOnlyDictionary<string, int> local, int itemQuality)
    {
        long Get(string name) => local.GetValueOrDefault(name);
        long quality = Get("local_quality_does_not_increase_defences") != 0 ? 0
            : Math.Max(0L, (long)itemQuality + Get("local_item_quality_+"));
        int Rating(int basis, long added, long increased)
        {
            // Native getters multiply the two single-precision factors first, then the base, add 0.5f
            // and truncate. Decimal rounding changes real tooltip results (e.g. 150 * 2.37f -> 355).
            float factor = Math.Max(0f, 1f + increased * 0.01f) * (1f + quality * 0.01f);
            float value = Math.Max(0L, basis + added) * factor + 0.5f;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
        long shared = Get("local_armour_and_evasion_+%") + Get("local_armour_and_evasion_and_energy_shield_+%");
        int armour = Rating(basis.Armour, Get("local_base_physical_damage_reduction_rating"),
            Get("local_physical_damage_reduction_rating_+%") + Get("local_armour_and_energy_shield_+%") + shared);
        int evasion = Rating(basis.Evasion, Get("local_base_evasion_rating") + Get("local_evasion_rating_and_energy_shield"),
            Get("local_evasion_rating_+%") + Get("local_evasion_and_energy_shield_+%") + shared);
        // Native 3.29.3c ES getter 0x1424b5f00: either prohibition wins over base, flat mods, increases and quality.
        int energyShield = Get("local_no_energy_shield") != 0
            || Get("local_unique_tabula_rasa_no_requirement_or_energy_shield") != 0 ? 0
            : Rating(basis.EnergyShield, Get("local_energy_shield") + Get("local_evasion_rating_and_energy_shield"),
                Get("local_energy_shield_+%") + Get("local_armour_and_energy_shield_+%")
                + Get("local_evasion_and_energy_shield_+%") + Get("local_armour_and_evasion_and_energy_shield_+%"));
        return (armour, evasion, energyShield);
    }
}

using GameServer.Core.Protocol.Instance;


/// <summary>The native item-tooltip defences, before character-wide modifiers and slot conversions.</summary>
static class ItemDefences
{
    public static (int Armour, int Evasion, int EnergyShield) Calculate(Item item, ItemCatalog.BaseDefences defences,
        ModCatalog mods, byte? baseRoll = null) =>
        CalculateLocal(defences.AtRoll(baseRoll ?? item.DefenceBaseRoll), ItemModStats.Local(item, mods), item.Quality);

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
            decimal value = Math.Max(0L, basis + added) * Math.Max(0m, 1 + increased / 100m)
                * (1 + quality / 100m);
            return (int)Math.Min(int.MaxValue, Math.Round(value, 0, MidpointRounding.AwayFromZero));
        }
        long shared = Get("local_armour_and_evasion_+%") + Get("local_armour_and_evasion_and_energy_shield_+%");
        int armour = Rating(basis.Armour, Get("local_base_physical_damage_reduction_rating"),
            Get("local_physical_damage_reduction_rating_+%") + Get("local_armour_and_energy_shield_+%") + shared);
        int evasion = Rating(basis.Evasion, Get("local_base_evasion_rating") + Get("local_evasion_rating_and_energy_shield"),
            Get("local_evasion_rating_+%") + Get("local_evasion_and_energy_shield_+%") + shared);
        // Native 3.29.3b ES getter 0x1424af430: either prohibition wins over base, flat mods, increases and quality.
        int energyShield = Get("local_no_energy_shield") != 0
            || Get("local_unique_tabula_rasa_no_requirement_or_energy_shield") != 0 ? 0
            : Rating(basis.EnergyShield, Get("local_energy_shield") + Get("local_evasion_rating_and_energy_shield"),
                Get("local_energy_shield_+%") + Get("local_armour_and_energy_shield_+%")
                + Get("local_evasion_and_energy_shield_+%") + Get("local_armour_and_evasion_and_energy_shield_+%"));
        return (armour, evasion, energyShield);
    }
}

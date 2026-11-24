public static partial class Game
{
    // Mastery effects that read the equipped shield or the reservations, which PassiveBonus does not see.
    static CharacterStats.Bonus WithSlotMasteries(Player player, CharacterStats.Bonus bonus, int shieldEnergyShield,
        Func<uint, int> get)
    {
        // Shield Mastery: "+1% to Critical Strike Multiplier per 10 Maximum Energy Shield on Shield".
        int multiplier = (int)Math.Clamp((long)get(Stat.CriticalMultiplierPlusPctPer10MaxEsOnShield)
            * (Math.Max(0, shieldEnergyShield) / 10), int.MinValue, int.MaxValue);
        // Reservation Mastery: "+1% to all maximum Elemental Resistances if you have Reserved Life and Mana", from the
        // reservation flags the stat sources captured (ItemConditionFlags).
        var flags = player.StatSourcesCache?.Key.Flags;
        bool reserved = flags is not null && !flags.GetValueOrDefault("_while_no_life_is_reserved", true)
            && !flags.GetValueOrDefault("_while_no_mana_reserved", true);
        int maximum = reserved ? get(Stat.AdditionalMaximumAllElementalResistancesPctWithReservedLifeAndMana) : 0;
        if (multiplier == 0 && maximum == 0) return bonus;
        return bonus with
        {
            CriticalStrikeMultiplier = bonus.CriticalStrikeMultiplier + multiplier,
            SpellCriticalStrikeMultiplier = bonus.SpellCriticalStrikeMultiplier + multiplier,
            FireMaxResistance = bonus.FireMaxResistance + maximum,
            ColdMaxResistance = bonus.ColdMaxResistance + maximum,
            LightningMaxResistance = bonus.LightningMaxResistance + maximum,
        };
    }
}

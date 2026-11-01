/// <summary>Flat maximum mana from complete groups of final Strength, before ordinary mana increases.</summary>
static class StrengthMana
{
    // 3.29.3b Stats.dat row 4992, client key 4993; Mental Conditioning graph node 38080 grants 8.
    public const string PerFourStrength = "X_mana_per_4_strength";

    public static long Flat(int finalStrength, int perFourStrength) =>
        Math.Max(0, finalStrength) / 4 * (long)perFourStrength;

    public static uint Maximum(long ordinaryFlatMana, int finalStrength, int perFourStrength, int increasedManaPct)
    {
        // Keep signed cancellation before clamping; a negative flat total and a negative multiplier cannot
        // resurrect mana. Decimal avoids overflow in the increase, and truncation keeps the existing mana rounding.
        // Native maximum_mana and the existing reservation/damage consumers use signed int values.
        decimal flat = Math.Max(0m, ordinaryFlatMana + (decimal)Flat(finalStrength, perFourStrength));
        return (uint)Math.Clamp(flat * Math.Max(0m, 100m + increasedManaPct) / 100m, 0m, int.MaxValue);
    }
}

using GameServer.Core.World;

/// <summary>
/// The increased effect for one flask use, from the original raw source snapshot and captured player level.
/// Nevalius' flat and per-level sources add once. The raw character keys remain raw; there is no effect panel row.
/// Local increased effect and magic-utility-only increased effect join the same additive bucket when applicable.
/// </summary>
static class FlaskEffectScaling
{
    public static long GlobalIncrease(Func<string, int> raw, int playerLevel = 1) =>
        (long)raw("flask_effect_+%")
        + (long)Math.Clamp(playerLevel, 1, Progression.MaxLevel) * raw("flask_effect_+%_per_level");

    /// <summary>Preserves the existing nonnegative multiplier; wide arithmetic avoids overflowing signed inputs.</summary>
    public static double Multiplier(Func<string, int> raw, int playerLevel = 1, int localIncrease = 0,
        int applicableMagicIncrease = 0) =>
        Math.Max(0L, 100L + GlobalIncrease(raw, playerLevel) + localIncrease + applicableMagicIncrease) / 100.0;
}

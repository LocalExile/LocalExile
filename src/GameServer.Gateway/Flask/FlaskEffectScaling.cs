using GameServer.Core.World;

/// <summary>
/// The increased effect for one flask use, from the original raw source snapshot and captured player level.
/// Nevalius' flat and per-level sources add once. The raw character keys remain raw; there is no effect panel row.
/// Local increased effect and magic-utility-only increased effect join the same additive bucket when applicable.
/// flask_effect_on_self_+% (an increased flask effect on the character, whatever source it comes from) joins the same
/// additive bucket.
/// </summary>
public static class FlaskEffectScaling
{
    public static long GlobalIncrease(Func<uint, int> raw, int playerLevel = 1) =>
        (long)raw(Stat.FlaskEffectPlusPct)
        + (long)Math.Clamp(playerLevel, 1, Progression.MaxLevel) * raw(Stat.FlaskEffectPlusPctPerLevel);

    /// <summary>Preserves the existing nonnegative multiplier; wide arithmetic avoids overflowing signed inputs.</summary>
    public static double Multiplier(Func<uint, int> raw, int playerLevel = 1, int localIncrease = 0,
        int applicableMagicIncrease = 0, double finalMultiplier = 1) =>
        Math.Max(0L, 100L + GlobalIncrease(raw, playerLevel) + localIncrease + applicableMagicIncrease
            + raw(Stat.FlaskEffectOnSelfPlusPct)) / 100.0
        * finalMultiplier;
}

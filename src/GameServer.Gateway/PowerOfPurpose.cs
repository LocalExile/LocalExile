using System.Numerics;

/// <summary>Power of Purpose's explicit PoB-based model, not a recovered official server algorithm.</summary>
static class PowerOfPurpose
{
    public const string RawFlag = "keystone_mental_conditioning";

    // All existing branches have denominators dividing this scale, including half a point of Intelligence mana.
    // BigInteger keeps signed contributions exact even when several int-sized modifiers would overflow decimal.
    public const int Scale = 1_000_000;

    /// <summary>
    /// Converts the single pre-increase flat mana bucket at 80%, granting twice the converted base as armour.
    /// The input is twice that bucket, so odd Intelligence retains its half mana without intermediate rounding.
    /// Outputs are signed numerators over Scale; combine Armour with ordinary contributions before RoundNative.
    /// A future flat mana source must join this input once, before either increased bucket is applied.
    /// </summary>
    public static (BigInteger Mana, BigInteger Armour) ConvertFlatMana(long rawFlatManaTwice,
        int manaPct, int armourPct, int armourMorePct = 0) =>
        ((BigInteger)rawFlatManaTwice * (100L + manaPct) * 1000,
            (BigInteger)rawFlatManaTwice * (100L + manaPct + armourPct) * (100L + armourMorePct) * 80);

    public static int RoundNative(BigInteger numerator) =>
        (int)BigInteger.Clamp((numerator + Scale / 2) / Scale, 0, int.MaxValue);

    public static (uint Mana, int Armour) Pools(long rawFlatManaTwice, CharacterStats.Bonus b)
    {
        var converted = ConvertFlatMana(rawFlatManaTwice, b.ManaPct, b.ArmourPct, b.ArmourMorePct);
        BigInteger armour = (BigInteger)(b.Armour * 100) * (100L + b.ArmourPct) * (100L + b.ArmourMorePct);
        if (b.IronReflexes)
            armour += (BigInteger)((CharacterStats.BaseEvasion + b.Evasion) * 100)
                * (100L + b.ArmourPct + b.EvasionPct + b.ColdOvercapEvasionPct - b.SharedDefencesPct)
                * (100L + b.EvasionMorePct) * (100L + b.ArmourMorePct) / 100;
        return ((uint)RoundNative(converted.Mana), RoundNative(armour + converted.Armour));
    }
}

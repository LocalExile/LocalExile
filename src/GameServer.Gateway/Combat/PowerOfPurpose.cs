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

    // Fractional local Ward conversion and independent MORE groups need arbitrary denominators. Keep those
    // fractions, signed cancellation and slot identity until the one final native rounding operation.
    readonly record struct Exact(BigInteger Numerator, BigInteger Denominator)
    {
        public static implicit operator Exact(decimal value)
        {
            int[] bits = decimal.GetBits(value);
            BigInteger n = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            return new((bits[3] & int.MinValue) != 0 ? -n : n, BigInteger.Pow(10, (bits[3] >> 16) & 255));
        }
        public static Exact operator +(Exact a, Exact b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static Exact operator *(Exact a, Exact b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
        public int Native() => (int)BigInteger.Clamp((2 * Numerator + Denominator) / (2 * Denominator), 0, int.MaxValue);
    }

    public static (uint Mana, int Armour) Pools(long rawFlatManaTwice, CharacterStats.Bonus b, int intelligence = 0)
    {
        Exact Slots(decimal total, decimal shield, decimal body, long increase, long shieldIncrease)
        {
            decimal bodyFactor = b.NoBodyArmour ? 0 : b.DoubleBodyArmour ? 2 : 1;
            return (Exact)(total - shield - body) * ((100m + increase) / 100m)
                + (Exact)(b.NoShieldDefences ? 0 : shield) * ((100m + increase + shieldIncrease) / 100m)
                + (Exact)body * bodyFactor * ((100m + increase) / 100m);
        }
        Exact manaBase = rawFlatManaTwice / 2m;
        Exact armourMore = (100m + b.ArmourMorePct) / 100m;
        Exact armour = Slots(b.Armour, b.ShieldArmour, b.BodyArmour, b.ArmourPct,
            (long)b.ShieldArmourPct + b.SharedShieldDefencesPct) * armourMore;
        if (b.IronReflexes)
            armour += Slots(CharacterStats.BaseEvasion + b.Evasion, b.ShieldEvasion, b.BodyEvasion,
                (long)b.ArmourPct + b.EvasionPct + b.ColdOvercapEvasionPct - b.SharedDefencesPct
                    + (long)b.EvasionPctPerTenIntelligence * (intelligence / 10),
                (long)b.ShieldArmourPct + b.ShieldEvasionPct + b.SharedShieldDefencesPct)
                * ((100m + b.EvasionMorePct) / 100m) * armourMore;
        Exact convertedArmour = manaBase * 1.6m * ((100m + b.ManaPct + b.ArmourPct) / 100m) * armourMore;
        return ((uint)(manaBase * .2m * ((100m + b.ManaPct) / 100m)).Native(), (armour + convertedArmour).Native());
    }
}

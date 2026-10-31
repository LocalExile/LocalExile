using System.Numerics;
using GameServer.Core.Protocol.Instance;


/// <summary>
/// LocalExile Fortification model v1, explicitly NOT the unknown GGG generation algorithm:
/// floor(20 * sqrt(rawHit / threshold) * rarityFactor * (1 + applicable increases / 100)), capped per hit at the
/// current maximum. Rarity factors are our policy: Normal 1, Magic 1.5, Rare 2, Unique 3. The default threshold is
/// maximum Life. rawHit is the accepted melee hit AFTER its critical multiplier and BEFORE target mitigation.
/// </summary>
static class FortificationGainModel
{
    public const int Version = 1;
    public const string GainedFromHitsStat = "fortification_gained_from_hits_+%";
    public const string GainedAgainstUniqueStat = "fortification_gained_from_hits_+%_against_unique_enemies";
    public readonly record struct Modifiers(int Increased = 0, int IncreasedAgainstUnique = 0)
    {
        public static Modifiers From(Func<string, int> get) => new(get(GainedFromHitsStat), get(GainedAgainstUniqueStat));
    }

    public static int Calculate(decimal rawHit, decimal maximumLife, MonsterRarity rarity, int maximum,
        Modifiers modifiers = default, decimal? ailmentThreshold = null)
    {
        int twiceRarity = rarity switch
        {
            MonsterRarity.Normal => 2, MonsterRarity.Magic => 3, MonsterRarity.Rare => 4, MonsterRarity.Unique => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity)),
        };
        decimal threshold = ailmentThreshold ?? maximumLife;
        long percent = Math.Max(0L, 100L + modifiers.Increased
            + (rarity == MonsterRarity.Unique ? modifiers.IncreasedAgainstUnique : 0L));
        if (rawHit <= 0 || threshold <= 0 || maximum <= 0 || percent == 0) return 0;
        var (damage, damageScale) = Fraction(rawHit);
        var (life, lifeScale) = Fraction(threshold);
        // Compare squares of exact rational values, avoiding platform sqrt and floating-point floor boundaries.
        BigInteger numerator = damage * lifeScale * twiceRarity * twiceRarity * percent * percent;
        BigInteger denominator = life * damageScale * 100;
        int low = 0, high = maximum;
        while (low < high)
        {
            int middle = low + (int)(((long)high - low + 1) / 2);
            if ((BigInteger)middle * middle * denominator <= numerator) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    static (BigInteger Numerator, BigInteger Denominator) Fraction(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger integer = (uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
        return (integer, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
    }
}

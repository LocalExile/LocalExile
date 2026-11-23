using System.Numerics;

/// <summary>Unconditional flat/increased accuracy. Keep the unrounded base so selected supports enter the
/// same buckets before flooring; never reverse a floored character rating to guess its base.</summary>
public static class AccuracyRating
{
    // Each weapon family's flat and increased accuracy stats (sword_accuracy_rating, sword_accuracy_rating_+%, …).
    static readonly string[] FamilyNames = ["sword", "axe", "mace", "claw", "dagger", "wand", "staff", "bow"];
    static readonly (uint Flat, uint Increased)[] FamilyStats =
        [.. FamilyNames.Select(f => (Stat.Of(f + "_accuracy_rating"), Stat.Of(f + "_accuracy_rating_+%")))];

    static IEnumerable<(uint Flat, uint Increased)> Families(WeaponProfile w)
    {
        bool[] held = [w.Sword, w.Axe, w.Mace, w.Claw, w.Dagger, w.Wand, w.Staff, w.Bow];
        for (int i = 0; i < held.Length; i++)
            if (held[i]) yield return FamilyStats[i];
    }

    public static long Flat(WeaponProfile weapon, Func<uint, long> get, bool includeLocal = true) =>
        get(Stat.AccuracyRating) + (includeLocal ? weapon.LocalAccuracy : 0L)
            + Families(weapon).Sum(family => get(family.Flat));

    public static long Increased(WeaponProfile weapon, Func<uint, long> get) =>
        get(Stat.AccuracyRatingPlusPct)
            + (weapon.DualWield || weapon.Unarmed ? get(Stat.AccuracyRatingWhileDualWieldingPlusPct) : 0)
            + (weapon.OneHandMelee ? get(Stat.OneHandedMeleeAccuracyRatingPlusPct) : 0)
            + (weapon.TwoHandMelee ? get(Stat.TwoHandedMeleeAccuracyRatingPlusPct) : 0)
            + Families(weapon).Sum(family => get(family.Increased));

    public static int Calculate(long basis, long increased) => (int)BigInteger.Min(int.MaxValue,
        BigInteger.Max(0, basis) * BigInteger.Max(0, (BigInteger)100 + increased) / 100);

    public static int Selected(CharacterStats.Pools pools, WeaponProfile weapon, Func<uint, long> own)
    {
        long flat = Flat(weapon, own, includeLocal: false), increased = Increased(weapon, own);
        return flat == 0 && increased == 0 ? pools.Accuracy
            : Calculate(pools.AccuracyBase + flat, pools.AccuracyPct + increased);
    }

    public static bool IsInput(uint name) => name is Stat.AccuracyRating or Stat.AccuracyRatingPlusPct
        or Stat.AccuracyRatingWhileDualWieldingPlusPct or Stat.OneHandedMeleeAccuracyRatingPlusPct
        or Stat.TwoHandedMeleeAccuracyRatingPlusPct or Stat.AccuracyRatingPerLevel
        or Stat.DexterityAccuracyBonusGrantsAccuracyRatingPlus3PerDexterityInstead
        or Stat.GainAccuracyRatingEqualToIntelligence or Stat.GainWandAccuracyRatingEqualToIntelligence
        or Stat.GainAccuracyRatingEqualTo2TimesStrength or Stat.GainAccuracyRatingEqualToStrength
        or Stat.AttackSpeedPlusPctPer200AccuracyRating
        or Stat.AccuracyRatingPlusPctFinalVsUniqueEnemiesFromMastery
        or Stat.AccuracyRatingPlusPctFinalVsEnemiesInCloseRangeFromMastery
        || FamilyStats.Any(family => name == family.Flat || name == family.Increased);
}

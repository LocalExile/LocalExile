using System.Numerics;

/// <summary>Unconditional flat/increased accuracy. Keep the unrounded base so selected supports enter the
/// same buckets before flooring; never reverse a floored character rating to guess its base.</summary>
static class AccuracyRating
{
    static IEnumerable<string> Families(WeaponProfile w)
    {
        if (w.Sword) yield return "sword";
        if (w.Axe) yield return "axe";
        if (w.Mace) yield return "mace";
        if (w.Claw) yield return "claw";
        if (w.Dagger) yield return "dagger";
        if (w.Wand) yield return "wand";
        if (w.Staff) yield return "staff";
        if (w.Bow) yield return "bow";
    }

    public static long Flat(WeaponProfile weapon, Func<string, long> get, bool includeLocal = true) =>
        get("accuracy_rating") + (includeLocal ? weapon.LocalAccuracy : 0L)
            + Families(weapon).Sum(family => get(family + "_accuracy_rating"));

    public static long Increased(WeaponProfile weapon, Func<string, long> get) =>
        get("accuracy_rating_+%")
            + (weapon.DualWield || weapon.Unarmed ? get("accuracy_rating_while_dual_wielding_+%") : 0)
            + (weapon.OneHandMelee ? get("one_handed_melee_accuracy_rating_+%") : 0)
            + (weapon.TwoHandMelee ? get("two_handed_melee_accuracy_rating_+%") : 0)
            + Families(weapon).Sum(family => get(family + "_accuracy_rating_+%"));

    public static int Calculate(long basis, long increased) => (int)BigInteger.Min(int.MaxValue,
        BigInteger.Max(0, basis) * BigInteger.Max(0, (BigInteger)100 + increased) / 100);

    public static int Selected(CharacterStats.Pools pools, WeaponProfile weapon, Func<string, long> own)
    {
        long flat = Flat(weapon, own, includeLocal: false), increased = Increased(weapon, own);
        return flat == 0 && increased == 0 ? pools.Accuracy
            : Calculate(pools.AccuracyBase + flat, pools.AccuracyPct + increased);
    }

    public static bool IsInput(string name) => name is "accuracy_rating" or "accuracy_rating_+%"
        or "accuracy_rating_while_dual_wielding_+%" or "one_handed_melee_accuracy_rating_+%"
        or "two_handed_melee_accuracy_rating_+%" or "accuracy_rating_per_level"
        or "dexterity_accuracy_bonus_grants_accuracy_rating_+3_per_dexterity_instead"
        or "gain_accuracy_rating_equal_to_intelligence" or "accuracy_rating_+%_final_vs_unique_enemies_from_mastery"
        or "accuracy_rating_+%_final_vs_enemies_in_close_range_from_mastery"
        || new[] { "sword", "axe", "mace", "claw", "dagger", "wand", "staff", "bow" }
            .Any(family => name == family + "_accuracy_rating" || name == family + "_accuracy_rating_+%");
}

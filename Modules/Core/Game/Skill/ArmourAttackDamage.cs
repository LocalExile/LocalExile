

/// <summary>Steel Willed and Forest Tracking: increased attack damage from complete groups of final defences.</summary>
public static class ArmourAttackDamage
{
    // 3.29.3b Stats.dat row 13018; Behemoth graph node 2103 grants 2.
    public const uint Per450Armour = Stat.AttackDamagePlusPctPer450PhysicalDamageReductionRating;
    // 3.29.3b Stats.dat row 2433; Daughter of Oshabi graph node 6281 grants 3.
    public const uint Per450Evasion = Stat.AttackDamagePlusPctPer450Evasion;

    public static int Total(int increasedAttackDamage, int finalArmour, int per450Armour,
        int finalEvasion = 0, int per450Evasion = 0)
    {
        // PoB 16de4b82 ModParser PerStat Armour/div and ModStore EvalMod floor the complete groups first.
        // Hit-specific armour doubling is not a change to the character's rating.
        // Keep both contributions wide until cancellation/addition is complete, then clamp the final stat once.
        long bonus = Math.Max(0, finalArmour) / 450 * (long)per450Armour
            + Math.Max(0, finalEvasion) / 450 * (long)per450Evasion;
        return (int)Math.Clamp(increasedAttackDamage + bonus, int.MinValue, int.MaxValue);
    }
}

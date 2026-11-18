/// <summary>The tree's keystones that change the evasion rating an enemy attack is rolled against (issue #1102).
/// Unwavering Stance: "Cannot Evade enemy Attacks". Arrow Dancing: "Evasion Rating is Doubled against Projectile
/// Attacks, 25% less Evasion Rating against Melee Attacks" — a ranged attack that is not a projectile keeps the rating
/// as it is, and a spell never rolls evasion at all.</summary>
public static class EvasionKeystones
{
    public static int Rating(int evasion, bool projectile, bool melee, Func<string, int> get) =>
        get("keystone_unwavering_stance") != 0 ? 0
        : get("keystone_projectile_evasion") == 0 ? evasion
        : projectile ? evasion * 2
        : melee ? (int)(evasion * 0.75)
        : evasion;
}

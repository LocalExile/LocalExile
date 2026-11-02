

// Native weapon-stat enum of main_hand_weapon_type / off_hand_weapon_type (Stats rows 2 and 3), decoded from the 3.29.3b
// client's stat calculators (13-items.md "Client weapon-type stats"): Bow is 9, whereas its ItemClasses row + 1 is 14 and
// its model stance is 8. An empty hand is 14. Network stats run no calculators (04-player-entity.md §4.1a), so the
// client derives neither these nor is_dual_wielding from them: the server sends all three. Without is_dual_wielding the
// client held 1, and its usability check (11-skills.md §10a) greys out a skill when dual wielding and the main hand does
// not fit it: Shield Charge, which only the off hand's shield fits.
public static class WeaponTypeStats
{
    public const int Shield = 13, EmptyHand = 14;

    static readonly Dictionary<string, int> ByClass = new()
    {
        ["One Hand Sword"] = 0, ["Thrusting One Hand Sword"] = 1, ["Two Hand Sword"] = 2,
        // is_wielding_mace takes 3 and 5 in either hand and 4 in the main hand only. Which of 3 and 5 is the sceptre is
        // not decoded (no client calculator tells them apart); the order of the swords suggests the one-handed mace first.
        ["One Hand Mace"] = 3, ["Two Hand Mace"] = 4, ["Sceptre"] = 5,
        ["Wand"] = 6, ["One Hand Axe"] = 7, ["Two Hand Axe"] = 8, ["Bow"] = 9, ["Dagger"] = 10, ["Staff"] = 11,
        ["Claw"] = 12, ["Shield"] = Shield, ["FishingRod"] = 15, ["Rune Dagger"] = 16, ["Warstaff"] = 17,
    };

    // A hand's value from its active item's ItemClasses Id; null (no item, or one whose requirements are not met) is an
    // empty hand. A quiver's own value is not decoded: it goes as an empty hand, which no calculator counts as a weapon.
    public static int Of(string? activeClass) =>
        activeClass is not null && ByClass.TryGetValue(activeClass, out int type) ? type : EmptyHand;

    // The client's is_dual_wielding calculator (0x142521190): a weapon in each hand (its table 0x142fdd728 is set for
    // every weapon value, not for the shield or an empty hand), or both hands empty with unarmed_counts_as_dual_wielding.
    public static bool IsDualWielding(int main, int off, bool unarmedCountsAsDualWielding) =>
        unarmedCountsAsDualWielding && main == EmptyHand && off == EmptyHand
        || main is not (Shield or EmptyHand) && off is not (Shield or EmptyHand);
}

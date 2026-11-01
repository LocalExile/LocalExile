/// <summary>The equipped weapon shape that passive conditions depend on (see PassiveBonus), from the item classes:
/// shields, dual wielding, staves/warstaves, bows, claws and the one- and two-handed weapon families.</summary>
readonly record struct WeaponProfile(bool Shield, bool DualWield, bool Staff, bool Bow, bool DualClaws,
    bool Unarmed = false, bool OneHand = false, bool TwoHand = false, bool Melee = false,
    bool OneHandMelee = false, bool TwoHandMelee = false,
    bool Axe = false, bool Sword = false, bool Mace = false, bool Claw = false, bool Dagger = false, bool Wand = false,
    int Critical = 0, int LocalAttackSpeedPct = 0, int LocalAccuracy = 0);

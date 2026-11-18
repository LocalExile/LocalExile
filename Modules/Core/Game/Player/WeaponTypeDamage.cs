using GameServer.Core.World;


/// <summary>The tree's "with &lt;weapon type&gt;" damage rows for one attacking hand (issue #1102, batch 1). The
/// spellings are the client's own: a family's plain row, its "hit and ailment" row, its ailment row, its physical,
/// elemental and per-element rows, and the rows two families read together ("Axe or Sword", "Mace or Staff", "Claw or
/// Dagger"), which the tree spells as separate ids rather than as aliases. A family a hand cannot hold counts nothing.
/// <para>
/// The plain, hit-and-ailment and ailment rows apply to every damage type, so they join the resolver's common bucket;
/// the physical, elemental and per-element rows are separate buckets and travel as a <see cref="HitIncreases"/>, so a
/// converted portion keeps each type it came from. The same rows reach the hit (<see cref="PlayerHitDamage"/>) and the
/// damaging ailments (<see cref="AilmentPanelStats"/>), which is why the ailment flag only adds the ailment-only ids.
/// </para>
/// </summary>
public static class WeaponTypeDamage
{
    /// <summary>The tree's family name for the item class of the attacking hand, null when the hand holds no weapon
    /// this build maps (a bow's own rows are read by the bow path, and an empty hand has none).</summary>
    public static string? FamilyOf(string? itemClass) => itemClass switch
    {
        "One Hand Mace" or "Two Hand Mace" or "Sceptre" => "mace",
        "One Hand Axe" or "Two Hand Axe" => "axe",
        "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword" => "sword",
        "Claw" => "claw",
        "Dagger" or "Rune Dagger" => "dagger",
        "Staff" or "Warstaff" => "staff",
        "Wand" => "wand",
        _ => null,
    };

    /// <summary>The same family from the native main_hand_weapon_type / off_hand_weapon_type value
    /// (<see cref="WeaponTypeStats"/>), which is what the ailment rows' context carries.</summary>
    public static string? FamilyOf(int weaponType) => weaponType switch
    {
        0 or 1 or 2 => "sword",
        3 or 4 or 5 => "mace",
        6 => "wand",
        7 or 8 => "axe",
        10 or 16 => "dagger",
        11 or 17 => "staff",
        12 => "claw",
        _ => null,
    };

    static bool TwoHanded(string? itemClass) => itemClass is "Two Hand Mace" or "Two Hand Axe" or "Two Hand Sword"
        or "Staff" or "Warstaff";

    static bool TwoHanded(int weaponType) => weaponType is 2 or 4 or 8 or 11 or 17;

    /// <summary>The rows that apply to every damage type of this hand's hits ("increased Damage with Maces",
    /// "increased Hit and Ailment Damage with Axes or Swords", and the tree's "with Two Handed / One Handed Weapons"
    /// rows, which are the Two Hand Mastery's 60% and its attack speed cost).</summary>
    public static int Any(SkillCatalog.Unarmed weapon, Func<string, int> get) =>
        Any(FamilyOf(weapon.ItemClass), TwoHanded(weapon.ItemClass), get, ailment: false);

    /// <summary>The physical, elemental and per-element rows of this hand's weapon type, in the shape
    /// <see cref="PlayerHitDamage.Resolve"/> applies after conversion.</summary>
    public static HitIncreases Of(SkillCatalog.Unarmed weapon, Func<string, int> get) =>
        Extra(FamilyOf(weapon.ItemClass), TwoHanded(weapon.ItemClass), get, ailment: false);

    /// <summary>The same rows for a damaging ailment of that hand: the ailment-only ids join the common bucket and the
    /// two-handed melee ailment row joins the physical one.</summary>
    public static (int Any, HitIncreases Extra) Ailment(int weaponType, Func<string, int> get) =>
        (Any(FamilyOf(weaponType), TwoHanded(weaponType), get, ailment: true),
            Extra(FamilyOf(weaponType), TwoHanded(weaponType), get, ailment: true));

    static int Any(string? family, bool twoHanded, Func<string, int> get, bool ailment)
    {
        if (family is null) return 0;
        int value = get($"{family}_damage_+%") + get($"{family}_hit_and_ailment_damage_+%");
        if (ailment) value += get($"{family}_ailment_damage_+%") + get($"attack_ailment_damage_+%_while_wielding_{family}");
        // The handedness rows are separate ids from the family's own, and the two-handed melee ones sit beside them.
        if (twoHanded)
        {
            value += get("damage_+%_with_two_handed_weapons") + get("two_handed_weapon_hit_and_ailment_damage_+%")
                + get("two_handed_melee_weapon_hit_and_ailment_damage_+%");
            if (ailment) value += get("two_handed_weapon_ailment_damage_+%")
                + get("attack_ailment_damage_+%_while_wielding_two_handed_weapon")
                + get("damage_over_time_+%_while_wielding_two_handed_weapon");
        }
        else
        {
            value += get("damage_+%_with_one_handed_weapons") + get("one_handed_weapon_hit_and_ailment_damage_+%");
            if (ailment) value += get("one_handed_weapon_ailment_damage_+%")
                + get("attack_ailment_damage_+%_while_wielding_one_handed_weapon");
        }
        return family switch
        {
            "mace" or "staff" => value + get("mace_or_staff_hit_and_ailment_damage_+%")
                + get("damage_+%_with_maces_sceptres_staves")
                + (ailment ? get("mace_or_staff_ailment_damage_+%") : 0),
            "axe" or "sword" => value + get("axe_or_sword_hit_and_ailment_damage_+%")
                + (ailment ? get("axe_or_sword_ailment_damage_+%")
                    + get("attack_skill_ailment_damage_+%_while_wielding_axes_swords") : 0),
            "claw" or "dagger" => value + get("claw_or_dagger_hit_and_ailment_damage_+%")
                + (ailment ? get("claw_or_dagger_ailment_damage_+%") : 0),
            "wand" => value + get("damage_+%_while_wielding_wand"),
            _ => value,
        };
    }

    static HitIncreases Extra(string? family, bool twoHanded, Func<string, int> get, bool ailment)
    {
        if (family is null) return default;
        int physical = get($"physical_{family}_damage_+%");
        if (family is "axe" or "sword") physical += get("physical_damage_+%_with_axes_swords");
        if (twoHanded)
        {
            physical += get("two_handed_melee_physical_damage_+%");
            if (ailment) physical += get("two_handed_melee_weapon_ailment_damage_+%");
        }
        int elemental = get($"{family}_elemental_damage_+%");
        List<(DamageTypeHistory, int)>? typed = null;
        foreach (var type in DamageTypeFamilies)
            if (get($"{type.ToString().ToLowerInvariant()}_{family}_damage_+%") is var value && value != 0)
                (typed ??= []).Add((DamageTypes.HistoryOf(type), value));
        if (twoHanded)
            foreach (var (type, stat) in new[] { (DamageType.Fire, "two_handed_melee_fire_damage_+%"),
                (DamageType.Cold, "two_handed_melee_cold_damage_+%") })
                if (get(stat) is var value && value != 0)
                    (typed ??= []).Add((DamageTypes.HistoryOf(type), value));
        return new(physical, elemental, typed);
    }

    static readonly DamageType[] DamageTypeFamilies = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
}

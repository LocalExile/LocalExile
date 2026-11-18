using GameServer.Core.World;


/// <summary>The tree's "with &lt;weapon type&gt;" damage increases for one attacking hand (issue #1102, batch 1). The
/// spellings are the client's own: a family's plain row, its "hit and ailment" row, its physical, elemental and
/// per-element rows, and the rows two families read together ("Axe or Sword", "Mace or Staff", "Claw or Dagger"), which
/// the tree spells as separate ids rather than as aliases. A family a hand cannot hold counts nothing.
/// <para>
/// The plain and hit-and-ailment rows apply to every damage type of the hit, so they join
/// <c>additionalIncreased</c>; the physical, elemental and per-element rows are separate buckets and travel as a
/// <see cref="HitIncreases"/>.
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

    static bool TwoHanded(string? itemClass) => itemClass is "Two Hand Mace" or "Two Hand Axe" or "Two Hand Sword"
        or "Staff" or "Warstaff";

    /// <summary>The increases that apply to every damage type of this hand's hits ("increased Damage with Maces",
    /// "increased Hit and Ailment Damage with Axes or Swords").</summary>
    public static int Any(SkillCatalog.Unarmed weapon, Func<string, int> get)
    {
        if (FamilyOf(weapon.ItemClass) is not { } family) return 0;
        int value = get($"{family}_damage_+%") + get($"{family}_hit_and_ailment_damage_+%");
        return family switch
        {
            "mace" or "staff" => value + get("mace_or_staff_hit_and_ailment_damage_+%")
                + get("damage_+%_with_maces_sceptres_staves"),
            "axe" or "sword" => value + get("axe_or_sword_hit_and_ailment_damage_+%"),
            "claw" or "dagger" => value + get("claw_or_dagger_hit_and_ailment_damage_+%"),
            "wand" => value + get("damage_+%_while_wielding_wand"),
            _ => value,
        };
    }

    /// <summary>The physical, elemental and per-element rows of this hand's weapon type, in the shape
    /// <see cref="PlayerHitDamage.Resolve"/> applies after conversion.</summary>
    public static HitIncreases Of(SkillCatalog.Unarmed weapon, Func<string, int> get)
    {
        if (FamilyOf(weapon.ItemClass) is not { } family) return default;
        int physical = get($"physical_{family}_damage_+%");
        if (TwoHanded(weapon.ItemClass)) physical += get("two_handed_melee_physical_damage_+%");
        if (family is "axe" or "sword") physical += get("physical_damage_+%_with_axes_swords");
        int elemental = get($"{family}_elemental_damage_+%");
        List<(DamageTypeHistory, int)>? typed = null;
        foreach (var type in DamageTypeFamilies)
            if (get($"{type.ToString().ToLowerInvariant()}_{family}_damage_+%") is var value && value != 0)
                (typed ??= []).Add((DamageTypes.HistoryOf(type), value));
        return new(physical, elemental, typed);
    }

    static readonly DamageType[] DamageTypeFamilies = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
}

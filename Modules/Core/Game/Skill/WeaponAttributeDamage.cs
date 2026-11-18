/// <summary>A character's attribute-scaled physical damage in a weapon attack or its ailment context.</summary>
public static class WeaponAttributeDamage
{
    public const string PerTenStrength = "physical_weapon_damage_+%_per_10_str";

    public static bool Armed(int type) => type is not (WeaponTypeStats.EmptyHand or WeaponTypeStats.Shield);
    public static bool Armed(SkillCatalog.Unarmed weapon) => weapon.ItemClass is not null;

    public static Func<string, int> Read(bool armed, Func<string, int> get)
    {
        long increase = armed ? (long)get(PerTenStrength) * (Math.Max(0, get("strength")) / 10) : 0;
        if (increase == 0) return get;
        // This is a typed character increase, including global added Physical and its converted portions. It must
        // never alter the item's local packet, which Battlemage and Spellblade can also use in a spell context.
        return name => name == "physical_damage_+%"
            ? (int)Math.Clamp(get(name) + increase, int.MinValue, int.MaxValue) : get(name);
    }
}

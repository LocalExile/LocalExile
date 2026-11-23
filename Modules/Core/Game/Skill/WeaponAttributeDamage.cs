/// <summary>A character's attribute-scaled physical damage in a weapon attack or its ailment context.</summary>
public static class WeaponAttributeDamage
{
    public const uint PerTenStrength = Stat.PhysicalWeaponDamagePlusPctPer10Str;

    public static bool Armed(int type) => type is not (WeaponTypeStats.EmptyHand or WeaponTypeStats.Shield);
    public static bool Armed(SkillCatalog.Unarmed weapon) => weapon.ItemClass is not null;

    public static Func<uint, int> Read(bool armed, Func<uint, int> get)
    {
        long increase = armed ? (long)get(PerTenStrength) * (Math.Max(0, get(Stat.Strength)) / 10) : 0;
        if (increase == 0) return get;
        // This is a typed character increase, including global added Physical and its converted portions. It must
        // never alter the item's local packet, which Battlemage and Spellblade can also use in a spell context.
        return name => name == Stat.PhysicalDamagePlusPct
            ? (int)Math.Clamp(get(name) + increase, int.MinValue, int.MaxValue) : get(name);
    }
}

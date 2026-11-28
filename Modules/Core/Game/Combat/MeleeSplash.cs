/// <summary>
/// Melee Splash: a melee strike's hit splashes onto the enemies around its target, with any support or inherent splash
/// damage multiplier. The splash base radius is 14 units (poewiki "Melee Splash Support": "The area of effect created
/// by Melee Splash has a base radius of 14 units"), grown by the skill's increased area (including
/// melee_splash_area_of_effect_+%) and more area multipliers (including melee_splash_area_of_effect_+%_final).
/// </summary>
public static class MeleeSplash
{
    public const int BaseRadius = 14;

    /// <summary>The splash radius and damage multiplier when a passive, item, support or tincture grants splash.</summary>
    public static (int Radius, double More)? Settings(Func<uint, int> get) => Settings(get, null);

    internal static (int Radius, double More)? Settings(Func<uint, int> get, int? inherentDamagePct)
    {
        int damage = get(Stat.SupportMeleeSplashDamagePlusPctFinalForSplash);
        int slayerDamage = get(Stat.SlayerAscendancyMeleeSplashDamagePlusPctFinalForSplash);
        bool tinctureApplied = get(Stat.TinctureApplied) != 0;
        bool tinctureSplash = tinctureApplied && get(Stat.MeleeSplashWithTincturedWeapons) != 0;
        if (get(Stat.MeleeSplash) == 0 && damage == 0 && slayerDamage == 0 && !tinctureSplash && inherentDamagePct is null)
            return null;
        int splashIncrease = get(Stat.MeleeSplashAreaOfEffectPlusPct)
            + (tinctureApplied ? get(Stat.WeaponMeleeSplashAreaOfEffectPlusPct) : 0);
        int AreaStat(uint key) => key == SkillArea.Increase
            ? ActorScale.Saturate((long)get(key) + splashIncrease) : get(key);
        double area = Math.Max(0, 1 + get(Stat.MeleeSplashAreaOfEffectPlusPctFinal) / 100.0);
        int radius = (int)Math.Floor(BaseRadius * Math.Sqrt(SkillArea.Modifier(AreaStat, spell: false) * area));
        double more = Math.Max(0, 1 + damage / 100d)
            * Math.Max(0, 1 + slayerDamage / 100d)
            * Math.Max(0, 1 + (inherentDamagePct ?? 0) / 100d);
        return (radius, more);
    }
}

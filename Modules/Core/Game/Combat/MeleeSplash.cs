/// <summary>
/// Melee Splash: a supported strike's hit splashes onto the enemies around its target, with the support's less damage.
/// The splash base radius is 14 units (poewiki "Melee Splash Support": "The area of effect created by Melee Splash
/// has a base radius of 14 units"), grown by the area modifier and the support's melee_splash_area_of_effect_+%_final.
/// </summary>
public static class MeleeSplash
{
    public const int BaseRadius = 14;

    /// <summary>The splash radius and damage multiplier, or null when the skill has no splash
    /// (neither Melee Splash Support nor an active Mightblood Ire tincture).</summary>
    public static (int Radius, double More)? Settings(Func<uint, int> get)
    {
        int damage = get(Stat.SupportMeleeSplashDamagePlusPctFinalForSplash);
        bool tinctureSplash = damage == 0 && get(Stat.MeleeSplashWithTincturedWeapons) != 0 && get(Stat.TinctureApplied) != 0;
        if (damage == 0 && !tinctureSplash) return null;
        double area = Math.Max(0, 1 + (get(Stat.MeleeSplashAreaOfEffectPlusPctFinal)
            + (tinctureSplash ? get(Stat.MeleeSplashAreaOfEffectPlusPct) : 0)) / 100.0);
        int radius = (int)Math.Floor(BaseRadius * Math.Sqrt(SkillArea.Modifier(get, spell: false) * area));
        return (radius, tinctureSplash ? 1.0 : Math.Max(0, 1 + damage / 100.0));
    }
}

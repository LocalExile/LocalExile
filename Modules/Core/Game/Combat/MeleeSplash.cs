/// <summary>
/// Melee Splash: a supported strike's hit splashes onto the enemies around its target, with the support's less damage.
/// The splash base radius is 14 units (poewiki "Melee Splash Support": "The area of effect created by Melee Splash
/// has a base radius of 14 units"), grown by the area modifier and the support's melee_splash_area_of_effect_+%_final.
/// </summary>
public static class MeleeSplash
{
    public const int BaseRadius = 14;

    /// <summary>The splash radius and damage multiplier, or null when the skill has no splash
    /// (support_melee_splash_damage_+%_final_for_splash absent; the support's value is negative).</summary>
    public static (int Radius, double More)? Settings(Func<string, int> get)
    {
        int damage = get("support_melee_splash_damage_+%_final_for_splash");
        if (damage == 0) return null;
        double area = Math.Max(0, 1 + get("melee_splash_area_of_effect_+%_final") / 100.0);
        int radius = (int)Math.Floor(BaseRadius * Math.Sqrt(SkillArea.Modifier(get, spell: false) * area));
        return (radius, Math.Max(0, 1 + damage / 100.0));
    }
}

/// <summary>
/// A skill's area of effect radius (Agents/docs/protocol/11-skills.md §12): the skill's
/// active_skill_base_area_of_effect_radius plus active_skill_base_radius_+, scaled by the square root of its area
/// modifier, as Path of Building's calcRadius does: floor(base × floor(100 × √(increased × more)) / 100). Increased:
/// base_skill_area_of_effect_+% (passives, gem quality), skill_area_of_effect_+% and, for spells, spell_area_of_effect_+%.
/// More: each of the "_final" area stats; active_skill_area_of_effect_radius_+%_final scales the radius itself. The
/// skill's stats include the player's. Conditional area stats (per charge, per attribute, "recently") are not modelled.
/// </summary>
static class SkillArea
{
    public static bool IsCyclone(string? effect) => effect is "Cyclone" or "CycloneAltX";

    /// <summary>Explicit data wins. Cyclone's base radius is not in its exported stat set (PoB: 16 cells).</summary>
    public static int BaseRadius(string? effect, Func<string, int> get) =>
        get("active_skill_base_area_of_effect_radius") is int radius and > 0 ? radius : IsCyclone(effect) ? 16 : 12;

    static readonly string[] MoreArea =
    [
        "active_skill_area_of_effect_+%_final", "skill_area_of_effect_+%_final",
        "support_concentrated_effect_skill_area_of_effect_+%_final", "skill_area_of_effect_+%_final_from_skill_specific_stat",
    ];

    /// <summary>The combined increased-area input before more multipliers. With <paramref name="combined"/>,
    /// skill_area_of_effect_+% is already the native final value including base_skill_area_of_effect_+%.</summary>
    public static int Increased(Func<string, int> get, bool spell, bool combined = false) =>
        (combined ? 0 : get("base_skill_area_of_effect_+%")) + get("skill_area_of_effect_+%") + (spell ? get("spell_area_of_effect_+%") : 0);

    /// <summary>The client's precomputed radius percentage (0x1591), not the increased area percentage.</summary>
    public static int RadiusPercent(Func<string, int> get, bool spell, bool combined = false) =>
        (int)Math.Floor(Math.Floor(100 * Math.Sqrt(Modifier(get, spell, combined)))
            * Math.Max(0, 1 + get("active_skill_area_of_effect_radius_+%_final") / 100.0)) - 100;

    /// <summary>The area modifier: (1 + increased) × each more multiplier, at least 0.</summary>
    public static double Modifier(Func<string, int> get, bool spell, bool combined = false)
    {
        int increased = Increased(get, spell, combined);
        double mod = Math.Max(0, 1 + increased / 100.0);
        foreach (string more in MoreArea) mod *= Math.Max(0, 1 + get(more) / 100.0);
        return mod;
    }

    /// <summary>The radius in cells for <paramref name="baseRadius"/> (the skill's base when it has none of its own).</summary>
    public static int Radius(int baseRadius, Func<string, int> get, bool spell, bool combined = false)
    {
        int radius = baseRadius + get("active_skill_base_radius_+");
        double scaled = radius * Math.Floor(100 * Math.Sqrt(Modifier(get, spell, combined))) / 100;
        scaled *= Math.Max(0, 1 + get("active_skill_area_of_effect_radius_+%_final") / 100.0);
        return Math.Max(0, (int)Math.Floor(scaled));
    }
}

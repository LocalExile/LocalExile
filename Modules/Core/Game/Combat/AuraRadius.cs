/// <summary>
/// An aura skill's radius in cells (Agents/docs/protocol/11-skills.md §13): every aura's base 40
/// (<see cref="AuraCatalog.BaseRadius"/>) plus the gem's active_skill_base_radius_+, scaled by the square root of its
/// area modifier as <see cref="SkillArea.Radius"/> does. The player's base_aura_area_of_effect_+% joins the gem's own
/// area (quality) as the base form of increased area.
/// </summary>
static class AuraRadius
{
    public static int Of(AuraCatalog.Aura aura, int auraAreaPct, SkillCatalog skills, StatCatalog stats)
    {
        var own = skills.EffectStats(aura.Effect, aura.Level, aura.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Get(string name) => (stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0)
            + (name == SkillArea.BaseIncreased ? auraAreaPct : 0);
        return SkillArea.Radius(AuraCatalog.BaseRadius, Get, spell: false);
    }
}

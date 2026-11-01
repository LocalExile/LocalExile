

/// <summary>
/// Converts the per-10-Devotion stats on Militant Faith (and any other item with such a stat) into the normal stats they
/// modify. The timeless tree already grants <c>base_devotion</c> from the allocated nodes in the jewel's radius; a stat
/// named <c>..._per_10_devotion</c> means "value per whole 10 Devotion", so 145 Devotion gives 14 steps (the same floor
/// as Path of Building's PerStat modifier).
///
/// The target is the client's normal stat for the same effect. All elemental resistance uses
/// <c>base_resist_all_elements_%</c>, which the resistance pool adds to fire, cold and lightning (not chaos). A target
/// that no other server code reads is still mapped here so the source is complete; the generated support list marks it
/// inert until a consumer exists.
/// </summary>
static class DevotionStats
{
    public const string Devotion = "base_devotion";

    sealed record Target(string Stat, int Factor = 1);

    static readonly IReadOnlyDictionary<string, Target[]> Targets = new Dictionary<string, Target[]>
    {
        ["totem_damage_+%_per_10_devotion"] = [new("totem_damage_+%")],
        ["sigil_damage_+%_per_10_devotion"] = [new("sigil_damage_+%")],
        ["channelled_skill_damage_+%_per_10_devotion"] = [new("channelled_skill_damage_+%")],
        ["area_damage_+%_per_10_devotion"] = [new("area_damage_+%")],
        ["elemental_damage_+%_per_10_devotion"] = [new("elemental_damage_+%")],
        ["elemental_resistance_%_per_10_devotion"] = [new("base_resist_all_elements_%")],
        ["non_damaging_ailment_effect_+%_per_10_devotion"] = [new("non_damaging_ailment_effect_+%")],
        ["self_elemental_status_duration_-%_per_10_devotion"] = [new("self_elemental_status_duration_-%")],
        ["self_curse_duration_+%_per_10_devotion"] = [new("self_curse_duration_+%")],
        ["minion_attack_and_cast_speed_+%_per_10_devotion"] = [new("minion_attack_and_cast_speed_+%")],
        ["minion_accuracy_rating_per_10_devotion"] = [new("minion_accuracy_rating")],
        ["mana_regeneration_rate_per_minute_per_10_devotion"] = [new("base_mana_regeneration_rate_per_minute")],
        ["mana_cost_+%_per_10_devotion"] = [new("mana_cost_+%")],
        ["base_mana_cost_efficiency_+%_per_10_devotion"] = [new("base_mana_cost_efficiency_+%")],
        ["non_curse_aura_effect_+%_per_10_devotion"] = [new("non_curse_aura_effect_+%")],
        ["shield_defences_+%_per_10_devotion"] = [new("combined_shield_defences_+%")],
    };

    /// <summary>Replaces every per-10-Devotion stat in <paramref name="sources"/> with its normal stat(s) times the
    /// number of whole 10 Devotion steps. The raw per-Devotion key is removed even at zero Devotion, so it cannot leak
    /// into a panel as a flat stat.</summary>
    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats)
    {
        int steps = stats.Key(Devotion) is uint devotionKey && sources.TryGetValue(devotionKey, out int devotion)
            ? Math.Max(0, devotion) / 10
            : 0;
        foreach (var (perStat, targets) in Targets)
        {
            if (stats.Key(perStat) is not uint perKey || !sources.TryGetValue(perKey, out int value)) continue;
            sources.Remove(perKey);
            if (steps == 0) continue;
            foreach (var target in targets)
                if (stats.Key(target.Stat) is uint targetKey)
                    sources[targetKey] = ActorScale.Add(targetKey, sources.GetValueOrDefault(targetKey),
                        value * steps * target.Factor);
        }
    }
}

public static partial class Game
{
    static void ApplyAbyssGaze(Dictionary<uint, int> sources, IReadOnlyDictionary<uint, int> replacements)
    {
        Add("arcane_surge_effect_on_self_+%_per_caster_abyss_jewel_up_to_+40%",
            "number_of_caster_abyss_jewels", "arcane_surge_effect_+%", 40);
        Add("projecitle_chance_to_be_able_to_chain_from_terrain_%_per_ranged_abyss_jewel_up_to_20%",
            "number_of_ranged_abyss_jewels", "projectile_chain_from_terrain_chance_%", 20);

        void Add(string input, string count, string target, int cap)
        {
            if (Stats.Key(input) is not uint coefficientKey) return;
            int coefficient = replacements.GetValueOrDefault(coefficientKey, sources.GetValueOrDefault(coefficientKey));
            if (coefficient <= 0 || Stats.Key(count) is not uint countKey || Stats.Key(target) is not uint targetKey
                || replacements.ContainsKey(targetKey)) return;
            int jewels = Math.Max(0, replacements.GetValueOrDefault(countKey, sources.GetValueOrDefault(countKey)));
            long bonus = Math.Min(cap, (long)jewels * coefficient);
            if (bonus > 0) sources[targetKey] = ActorScale.Saturate(sources.GetValueOrDefault(targetKey) + bonus);
        }
    }
}

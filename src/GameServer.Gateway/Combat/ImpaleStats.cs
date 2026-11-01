/// <summary>Unconditional impale inputs, kept separate from final panel aliases and target mitigation.</summary>
static class ImpaleStats
{
    public static bool IsDisplay(string stat) => stat is "virtual_total_main_hand_impale_on_hit_%_chance"
        or "virtual_total_off_hand_impale_on_hit_%_chance" or "virtual_total_spell_impale_on_hit_%_chance";

    public readonly record struct Settings(int Chance, int EffectPercent = 0,
        int Hits = ImpaleState.HitsPerStack, double Duration = ImpaleState.BaseDuration);

    public static Settings From(IReadOnlyDictionary<uint, int> character, StatCatalog catalog, bool attack,
        IReadOnlyDictionary<uint, int>? own = null, IReadOnlyDictionary<string, int>? weaponLocal = null,
        bool critical = false)
    {
        long Get(string name) => catalog.Key(name) is uint key
            ? (long)character.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0) : 0;
        long chance = Get("impale_on_hit_%_chance") + (attack
            ? Get("attacks_impale_on_hit_%_chance") + (weaponLocal?.GetValueOrDefault("local_attacks_impale_on_hit_%_chance") ?? 0)
            : Get("spells_impale_on_hit_%_chance") + (critical ? Get("spell_impale_on_crit_%_chance") : 0));
        long effect = Get("impale_debuff_effect_+%") + (attack ? 0 : Get("impale_effect_+%_for_impales_inficted_by_spells"));
        return new((int)Math.Clamp(chance, 0, 100), (int)Math.Clamp(effect, int.MinValue, int.MaxValue),
            (int)Math.Clamp(ImpaleState.HitsPerStack + Get("impaled_debuff_number_of_reflected_hits"), 0, int.MaxValue),
            ImpaleState.BaseDuration * Math.Max(0, 1 + Get("impaled_debuff_duration_+%") / 100.0));
    }

    public static int AddReflected(int direct, int reflected) => (int)Math.Clamp((long)direct + reflected, 0, int.MaxValue);
}

using GameServer.Core.World;

/// <summary>The current SummonedWolf variant, not the obsolete DisplayMinionMonsterTypes[8] Lion model.
/// Variety multipliers, level, duration, limit and owner buff come from the pinned client tables.</summary>
static class SpectralWolf
{
    public const string Effect = "SummonSpectralWolfTriggered";
    public const string Variety = "Metadata/Monsters/SummonedWolf/SummonedWolf";
    public const ushort Buff = 1559;

    public sealed record Values(byte Level, int Limit, int DurationMs, int CooldownMs, int BuffMin, int BuffMax);

    public static Values? For(SkillCatalog skills, StatCatalog stats, int effect, int level,
        Func<string, int> owner)
    {
        var own = skills.EffectStats(effect, level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Get(string name) => stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        if (skills.EffectId(effect) != Effect) return null;
        return new((byte)Math.Clamp(Get("base_display_minion_actor_level"), 1, 100),
            Math.Max(0, Get("number_of_wolves_allowed") + owner("number_of_wolves_allowed")),
            Math.Max(0, (int)(Get("base_skill_effect_duration")
                * Math.Max(0, 100L + owner("skill_effect_duration_+%")) / 100)), skills.CooldownMs(effect, level),
            Get("spectral_wolf_grants_attack_minimum_added_physical_damage"),
            Get("spectral_wolf_grants_attack_maximum_added_physical_damage"));
    }

    public static int DamageIncrease(Func<string, int> get, int dexterity) => get("minion_damage_+%")
        + (int)Math.Floor(dexterity / 5.0) * get("minion_damage_+%_per_5_dex");

    public static (int Chance, int Multiplier) Critical(Func<string, int> owner, Func<string, int> skill)
    {
        int increase = owner("minion_critical_strike_chance_+%");
        int multiplier = 130 + owner("minion_critical_strike_multiplier_+");
        if (skill("modifiers_to_claw_critical_strike_chance_apply_minion_critical_strike_chance") != 0)
            increase += owner("claw_critical_strike_chance_+%");
        if (skill("modifiers_to_claw_critical_strike_multiplier_apply_minion_critical_strike_multiplier") != 0)
            multiplier += owner("claw_critical_strike_multiplier_+");
        if (owner("minion_no_critical_strike_multiplier") != 0) multiplier = 100;
        return (Math.Clamp((int)((500L + owner("minion_additional_base_critical_strike_chance"))
            * Math.Max(0, 100L + increase) / 100), 0, 10000), Math.Max(0, multiplier));
    }
}

/// <summary>
/// Warcry actions read raw GE CastTime and warcry speed (0x141abcaa0), then common animation applies action speed
/// (0x141e99bd0). The native nonspell rate callback (0x14278ab90) excludes action speed and timer quantization.
/// </summary>
readonly record struct WarcryTiming(int BaseMilliseconds, int IncreasedWarcrySpeed, double ActionMultiplier,
    bool Instant, int? DurationMilliseconds, int HundredTimesUsesPerSecond, int NativeNonSpellRate)
{
    static readonly string[] Unsupported = [
        "base_spell_cast_time_ms_override", "warcry_speed_+%_if_not_warcried_recently",
        "skill_is_retaliation_skill", "skill_is_steel_skill_reload",
    ];

    public static WarcryTiming? ForSkill(SkillCatalog skills, StatCatalog stats, int effect, int level, int quality,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own = null)
    {
        if (!skills.IsWarcry(effect)) return null;
        own ??= skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Get(IReadOnlyDictionary<uint, int> values, string name) =>
            stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;
        if (Unsupported.Any(name => Get(player, name) != 0 || Get(own, name) != 0)) return null;
        int speed = (int)Math.Clamp((long)Get(player, "warcry_speed_+%") + Get(own, "warcry_speed_+%"),
            int.MinValue, int.MaxValue);
        bool instant = skills.IsInstant(effect) || Get(player, "base_skill_is_instant") != 0
            || Get(own, "base_skill_is_instant") != 0 || Get(player, "warcries_are_instant") != 0
            || Get(own, "warcries_are_instant") != 0;
        return Calculate(skills.CastMs(effect), speed, ActionSpeed.Multiplier(player, stats.Key), instant);
    }

    public static WarcryTiming Calculate(int baseMilliseconds, int increasedWarcrySpeed,
        double actionMultiplier = 1, bool instant = false)
    {
        // Reuse only the server's final integer-ms quantization, keeping native publication separate.
        var time = SpellCastTiming.Calculate(baseMilliseconds, increasedWarcrySpeed, instant, actionMultiplier);
        decimal rawRate = baseMilliseconds > 0
            ? 1000m * (100m + increasedWarcrySpeed) / baseMilliseconds : 0;
        int nativeRate = (int)Math.Clamp(decimal.Round(rawRate, 0, MidpointRounding.AwayFromZero),
            int.MinValue, int.MaxValue);
        return new(baseMilliseconds, increasedWarcrySpeed, actionMultiplier, instant,
            time.DurationMilliseconds, time.HundredTimesCastsPerSecond, nativeRate);
    }

    public void ApplyTo(IDictionary<uint, int> stats, StatCatalog catalog)
    {
        foreach (var (name, value) in new[]
        {
            ("base_spell_cast_time_ms", BaseMilliseconds),
            ("virtual_base_spell_cast_time_ms", BaseMilliseconds),
            ("warcry_speed_+%", IncreasedWarcrySpeed),
            ("is_warcry", 1),
            ("skill_is_instant", Instant ? 1 : 0),
            ("hundred_times_non_spell_casts_per_second", NativeNonSpellRate),
        })
            if (catalog.Key(name) is uint key) stats[key] = value;
    }
}

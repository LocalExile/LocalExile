

/// <summary>Native spell actions apply cast speed to the base milliseconds themselves. Only server waits and
/// displayed casts per second use the adjusted duration.</summary>
readonly record struct SpellCastTiming(int BaseMilliseconds, int? DurationMilliseconds, int HundredTimesCastsPerSecond)
{
    public void ApplyTo(IDictionary<uint, int> stats, StatCatalog catalog)
    {
        foreach (var (name, value) in new[]
        {
            ("base_spell_cast_time_ms", BaseMilliseconds),
            ("virtual_base_spell_cast_time_ms", BaseMilliseconds),
            ("hundred_times_casts_per_second", HundredTimesCastsPerSecond),
        })
            if (catalog.Key(name) is uint key) stats[key] = value;
    }

    public static SpellCastTiming? ForSkill(SkillCatalog skills, int effect, int increasedCastSpeed) =>
        skills.IsSpell(effect) && !skills.IsAttack(effect)
            ? Calculate(skills.CastMs(effect), increasedCastSpeed, skills.IsInstant(effect)) : null;

    public static SpellCastTiming Calculate(int baseMilliseconds, int increasedCastSpeed, bool instant = false)
    {
        if (instant || baseMilliseconds == 0) return new(baseMilliseconds, 0, 0);
        long factor = 100L + increasedCastSpeed;
        if (baseMilliseconds < 0 || factor <= 0) return new(baseMilliseconds, null, 0);
        decimal adjusted = baseMilliseconds * 100m / factor;
        // Numeric bounds for the existing integer-ms server timers, not an official cast-speed cap.
        if (adjusted > int.MaxValue) return new(baseMilliseconds, null, 0);
        int duration = Math.Max(1, (int)decimal.Round(adjusted));
        return new(baseMilliseconds, duration, (int)Math.Round(100_000.0 / duration));
    }
}

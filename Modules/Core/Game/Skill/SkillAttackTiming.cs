

/// <summary>Ordinary weapon-based attack timing with the gem's inherent GEPL attack-speed multiplier.</summary>
static class SkillAttackTiming
{
    public static double ForSkill(SkillCatalog skills, int effect, int level, SkillCatalog.Unarmed weapon,
        IReadOnlyDictionary<uint, int> player) => skills.IsAttack(effect)
        ? Seconds(weapon.AttackMs, weapon.IncreasedAttackSpeed(player), skills.AttackSpeedMultiplier(effect, level))
        : weapon.AttackSeconds(player);

    public static void Apply(SkillCatalog skills, int effect, int level, Dictionary<uint, int> stats)
    {
        if (skills.IsAttack(effect)) Apply(stats, skills.AttackSpeedMultiplier(effect, level));
    }

    // The server's own integer base duration, resolved once, before applying player speed and the server's minimum
    // attack duration. The client applies the inherent speed itself, so this value is not what it is sent (Apply).
    public static int BaseMilliseconds(int weaponBaseMs, int inherentPercent)
    {
        if (inherentPercent <= -100) inherentPercent = 0; // Invalid data cannot create a non-finite duration.
        return (int)Math.Clamp(Math.Round(Math.Max(1, weaponBaseMs) / (1 + inherentPercent / 100.0),
            MidpointRounding.AwayFromZero), 1, int.MaxValue);
    }

    public static double Seconds(int weaponBaseMs, int playerSpeedPercent, int inherentPercent) =>
        Math.Max(Program.TickSeconds, BaseMilliseconds(weaponBaseMs, inherentPercent) / 1000.0
            / Math.Max(.01, 1 + playerSpeedPercent / 100.0));

    // Only the panel's attack rate changes. The total base durations stay the weapon's: the client applies the gem's
    // inherent speed to them itself. Sending Burning Arrow 1020 ms played its clip for 1.457 s, with the slow section
    // stretched and the muzzle flash 325 ms after the arrow; 714 ms plays it for 1.02 s (Frida 2026-10-31).
    public static void Apply(Dictionary<uint, int> stats, int inherentPercent)
    {
        if (!stats.TryGetValue(StatKeys.MainHandAttackDurationMs, out int main) || main <= 0) return;
        stats[StatKeys.HundredTimesAttacksPerSecond] = (int)Math.Round(100
            / Seconds(main, stats.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct), inherentPercent));
    }
}

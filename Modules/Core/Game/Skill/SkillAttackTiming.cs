

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

    // Native 0x14270a02b..0x14270a0a3: float multiply, round halves away from zero, then floor at -99 percent.
    // Both the action's final speed and its attack-rate row must use this quantized value.
    public static int FinalSpeedPercent(int increased, double more) =>
        Math.Max(-99, (int)MathF.Round((increased + 100) * (float)more - 100, MidpointRounding.AwayFromZero));

    public static double Seconds(int weaponBaseMs, int playerSpeedPercent, int inherentPercent) =>
        Math.Max(Program.TickSeconds, Math.Max(1, weaponBaseMs) / 1000.0
            / (1 + FinalSpeedPercent(playerSpeedPercent, inherentPercent <= -100 ? 1 : 1 + inherentPercent / 100.0) / 100.0));

    // The total base durations stay the weapon's; the skill publisher carries inherent speed in the final percent.
    // Sending Burning Arrow 1020 ms played its clip for 1.457 s, with the slow section
    // stretched and the muzzle flash 325 ms after the arrow; 714 ms plays it for 1.02 s (Frida 2026-10-31).
    public static void Apply(Dictionary<uint, int> stats, int inherentPercent)
    {
        if (!stats.TryGetValue(StatKeys.MainHandAttackDurationMs, out int main) || main <= 0) return;
        stats[StatKeys.HundredTimesAttacksPerSecond] = (int)Math.Round(100
            / Seconds(main, stats.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct), inherentPercent));
    }
}

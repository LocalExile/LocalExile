/// <summary>Ordinary weapon-based attack timing with the gem's inherent GEPL attack-speed multiplier.</summary>
static class SkillAttackTiming
{
    public static double ForSkill(SkillCatalog skills, int effect, int level, SkillCatalog.Unarmed weapon,
        IReadOnlyDictionary<uint, int> player) => skills.IsAttack(effect)
        ? Seconds(weapon.AttackMs, weapon.IncreasedAttackSpeed(player), skills.AttackSpeedMultiplier(effect, level),
            ActionSpeed.Multiplier(player, Program.Stats.Key))
        : weapon.AttackSeconds(player);

    public static void Apply(SkillCatalog skills, int effect, int level, Dictionary<uint, int> stats)
    {
        if (skills.IsAttack(effect)) Apply(stats, skills.AttackSpeedMultiplier(effect, level));
    }

    // The native action reads an integer total base duration separately from the player's final increased speed.
    // Resolve that integer once, before applying player speed and the server's minimum attack duration.
    public static int BaseMilliseconds(int weaponBaseMs, int inherentPercent)
    {
        if (inherentPercent <= -100) inherentPercent = 0; // Invalid data cannot create a non-finite duration.
        return (int)Math.Clamp(Math.Round(Math.Max(1, weaponBaseMs) / (1 + inherentPercent / 100.0),
            MidpointRounding.AwayFromZero), 1, int.MaxValue);
    }

    public static double Seconds(int weaponBaseMs, int playerSpeedPercent, int inherentPercent, double actionMultiplier = 1) =>
        actionMultiplier <= 0 || !double.IsFinite(actionMultiplier) ? double.PositiveInfinity
            : Math.Max(Program.TickSeconds, BaseMilliseconds(weaponBaseMs, inherentPercent) / 1000.0
                / Math.Max(.01, 1 + playerSpeedPercent / 100.0) / actionMultiplier);

    public static void Apply(Dictionary<uint, int> stats, int inherentPercent)
    {
        if (!stats.TryGetValue(StatKeys.MainHandAttackDurationMs, out int main) || main <= 0) return;
        stats[StatKeys.MainHandTotalAttackDurationMs] = BaseMilliseconds(main, inherentPercent);
        stats[StatKeys.HundredTimesAttacksPerSecond] = (int)Math.Round(100
            / Seconds(main, stats.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct), inherentPercent,
                ActionSpeed.Multiplier(stats, Program.Stats.Key)));
        if (stats.TryGetValue(StatKeys.OffHandAttackDurationMs, out int off) && off > 0)
            stats[StatKeys.OffHandTotalAttackDurationMs] = BaseMilliseconds(off, inherentPercent);
    }
}

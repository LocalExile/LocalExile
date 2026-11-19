

/// <summary>Weapon-based attack timing with additional base time and the gem's inherent GEPL attack-speed multiplier.</summary>
public static class SkillAttackTiming
{
    public const string AdditionalBaseAttackTime = "additional_weapon_base_attack_time_ms";

    public static int AdditionalMilliseconds(IReadOnlyDictionary<uint, int> stats) =>
        Program.Stats.Key(AdditionalBaseAttackTime) is uint key ? stats.GetValueOrDefault(key) : 0;

    /// <summary><paramref name="own"/> is the skill's unmerged stat set, or the catalog's own rows when omitted.</summary>
    public static double ForSkill(SkillCatalog skills, int effect, int level, SkillCatalog.Unarmed weapon,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own = null, int localSpeedInPercent = 0,
        double actionSpeed = 1)
    {
        if (!skills.IsAttack(effect)) return Math.Max(Program.TickSeconds, weapon.AttackSeconds(player) / actionSpeed);
        int skillAdditional = own is not null ? AdditionalMilliseconds(own)
            : Program.Stats.Key(AdditionalBaseAttackTime) is uint key
                ? skills.EffectStats(effect, level).FirstOrDefault(s => s.Item1 == key).Item2 : 0;
        int additional = (int)Math.Clamp((long)AdditionalMilliseconds(player) + skillAdditional, int.MinValue, int.MaxValue);
        int inherent = skills.AttackSpeedMultiplier(effect, level);
        // The legacy caller divides action speed after the weapon's tick minimum; retain that order for stat zero.
        return additional == 0
            ? Math.Max(Program.TickSeconds, Seconds(weapon.AttackMs, weapon.IncreasedAttackSpeed(player), inherent) / actionSpeed)
            : Seconds(weapon.AttackMs, weapon.IncreasedAttackSpeed(player), inherent, additional, actionSpeed,
                localSpeedInPercent: localSpeedInPercent);
    }

    public static void Apply(SkillCatalog skills, int effect, int level, Dictionary<uint, int> stats, int localSpeedInPercent = 0)
    {
        if (skills.IsAttack(effect)) Apply(stats, skills.AttackSpeedMultiplier(effect, level), localSpeedInPercent);
    }

    // Native 0x14270a02b..0x14270a0a3: float multiply, round halves away from zero, then floor at -99 percent.
    // Both the action's final speed and its attack-rate row must use this quantized value.
    public static int FinalSpeedPercent(int increased, double more) =>
        Math.Max(-99, (int)MathF.Round((increased + 100) * (float)more - 100, MidpointRounding.AwayFromZero));

    /// <summary>The item duration already includes local attack speed; additional time joins it afterwards.</summary>
    public static int BaseMilliseconds(int weaponLocalMs, int additionalMilliseconds = 0) =>
        (int)Math.Clamp((long)Math.Max(1, weaponLocalMs) + additionalMilliseconds, 1, int.MaxValue);

    public static double Seconds(int weaponBaseMs, int playerSpeedPercent, int inherentPercent,
        int additionalMilliseconds = 0, double actionSpeed = 1, bool clampToTick = true, int localSpeedInPercent = 0) =>
        Seconds(weaponBaseMs, playerSpeedPercent, inherentPercent <= -100 ? 1 : 1 + inherentPercent / 100.0,
            additionalMilliseconds, actionSpeed, clampToTick, localSpeedInPercent);

    /// <summary><paramref name="weaponLocalMs"/> includes item-local speed. <paramref name="playerSpeedPercent"/>
    /// is the shared character speed; pass <paramref name="localSpeedInPercent"/> only if that bucket also includes a
    /// local multiplier. The zero-additional-time path retains its legacy speed and rounding.</summary>
    public static double Seconds(int weaponLocalMs, int playerSpeedPercent, double more,
        int additionalMilliseconds = 0, double actionSpeed = 1, bool clampToTick = true, int localSpeedInPercent = 0)
    {
        int speed = additionalMilliseconds != 0 && localSpeedInPercent != 0
            ? SkillCatalog.Unarmed.OffHandAttackSpeedPct(playerSpeedPercent, localSpeedInPercent) : playerSpeedPercent;
        double seconds = BaseMilliseconds(weaponLocalMs, additionalMilliseconds) / 1000.0
            / (1 + FinalSpeedPercent(speed, more) / 100.0) / actionSpeed;
        return Math.Max(clampToTick ? Program.TickSeconds : .001, seconds);
    }

    // The weapon's base duration stays local-speed-adjusted. Native 0x14270bce0 adds the extra milliseconds to its
    // total base duration before global speed; GEPL inherent speed remains in the final percent, not the duration.
    public static void Apply(Dictionary<uint, int> stats, int inherentPercent, int localSpeedInPercent = 0)
    {
        if (!stats.TryGetValue(Stat.MainHandBaseWeaponAttackDurationMs, out int main) || main <= 0) return;
        int additional = AdditionalMilliseconds(stats);
        if (additional != 0)
        {
            stats[Stat.MainHandTotalBaseWeaponAttackDurationMs] = BaseMilliseconds(main, additional);
            if (stats.TryGetValue(Stat.OffHandBaseWeaponAttackDurationMs, out int off) && off > 0)
                stats[Stat.OffHandTotalBaseWeaponAttackDurationMs] = BaseMilliseconds(off, additional);
        }
        stats[Stat.HundredTimesAttacksPerSecond] = (int)Math.Round(100
            / Seconds(main, stats.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct), inherentPercent, additional,
                localSpeedInPercent: localSpeedInPercent));
    }
}

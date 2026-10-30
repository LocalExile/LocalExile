using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.World;

/// <summary>
/// Character progression rules that need no game files: the experience a kill gives (with Path of Exile's level
/// penalty), the level an amount of experience reaches, and the passive skill points a level gives. The tables
/// (ExperienceLevels.dat, DefaultMonsterStats.dat) are passed in by the caller. Docs: Agents/docs/protocol/15-progression.md.
/// </summary>
public static class Progression
{
    /// <summary>The client's/table's hard ceiling. The configurable <see cref="MaxLevel"/> may lower it.</summary>
    public const int MaxLevelCeiling = 100;

    static int _maxLevel = MaxLevelCeiling;

    /// <summary>The active level cap. The Gateway sets it from the system settings; lowering it clamps new level
    /// calculations and the passive-point budget, while the experience table and client remain at 100.</summary>
    public static int MaxLevel
    {
        get => Volatile.Read(ref _maxLevel);
        set => Volatile.Write(ref _maxLevel, Math.Clamp(value, 1, MaxLevelCeiling));
    }

    static double _experienceMultiplier = 1;

    /// <summary>A global multiplier on monster experience. The Gateway sets it from the system settings; 1 is the
    /// live game's value.</summary>
    public static double ExperienceMultiplier
    {
        get => Volatile.Read(ref _experienceMultiplier);
        set => Volatile.Write(ref _experienceMultiplier, Math.Clamp(value, 0, 1000));
    }

    /// <summary>
    /// The level penalty: monsters within a safe zone of 3 + level / 16 levels give full experience; beyond it the
    /// experience is scaled by ((L + 5) / (L + 5 + d^2.5))^1.5, d the levels past the safe zone (L the player's level).
    /// The extra penalty of levels 95 and above is not modelled.
    /// </summary>
    public static double LevelPenalty(int playerLevel, int monsterLevel)
    {
        int safeZone = 3 + playerLevel / 16;
        int d = Math.Max(Math.Abs(playerLevel - monsterLevel) - safeZone, 0);
        if (d == 0) return 1;
        double l = playerLevel + 5;
        return Math.Max(0.01, Math.Pow(l / (l + Math.Pow(d, 2.5)), 1.5));
    }

    /// <summary>
    /// How much more experience a monster of this rarity gives than a normal one. Our own numbers (the client's are
    /// not found yet): magic ×2, rare ×5, unique ×5.
    /// </summary>
    public static double RarityMultiplier(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 2,
        MonsterRarity.Rare or MonsterRarity.Unique => 5,
        _ => 1,
    };

    /// <summary>
    /// The experience a kill gives: DefaultMonsterStats' Experience at the monster's level × the variety's
    /// ExperienceMultiplier (percent) × the rarity multiplier × the level penalty, rounded, at least 1 when the base is.
    /// </summary>
    public static uint MonsterExperience(int baseExperience, int multiplierPercent, MonsterRarity rarity, int playerLevel, int monsterLevel)
    {
        if (baseExperience <= 0 || multiplierPercent <= 0) return 0;
        double experienceMultiplier = ExperienceMultiplier;
        if (experienceMultiplier <= 0) return 0;
        double xp = baseExperience * (multiplierPercent / 100.0) * RarityMultiplier(rarity)
            * LevelPenalty(playerLevel, monsterLevel) * experienceMultiplier;
        return (uint)Math.Max(1, Math.Round(xp));
    }

    /// <summary>The level an amount of experience reaches: the highest level whose start (<paramref name="levelStarts"/>,
    /// index = level − 1, ExperienceLevels.dat) is at most it.</summary>
    public static int LevelFor(uint experience, IReadOnlyList<uint> levelStarts)
    {
        int level = 1;
        for (int i = 1; i < levelStarts.Count && i < MaxLevel; i++)
            if (levelStarts[i] <= experience) level = i + 1;
            else break;
        return level;
    }

    /// <summary>Adds experience, capped at the start of the last level (no experience is gained at level 100).</summary>
    public static uint AddExperience(uint experience, uint gain, IReadOnlyList<uint> levelStarts)
    {
        uint cap = levelStarts.Count == 0 ? uint.MaxValue : levelStarts[Math.Min(levelStarts.Count, MaxLevel) - 1];
        return (uint)Math.Min(cap, (ulong)experience + gain);
    }

    static int _passivePointsPerLevel = 1;

    /// <summary>Passive skill points granted per level after the first. The Gateway sets it from the system settings;
    /// the default is 1, the live game's value.</summary>
    public static int PassivePointsPerLevel
    {
        get => Volatile.Read(ref _passivePointsPerLevel);
        set => Volatile.Write(ref _passivePointsPerLevel, Math.Clamp(value, 1, 100));
    }

    /// <summary>Passive skill points from the character's level: <see cref="PassivePointsPerLevel"/> per level after the
    /// first (quests are not implemented, so they give none).</summary>
    public static int PassivePoints(int level) => PassivePoints(level, PassivePointsPerLevel);

    /// <summary>The same calculation with an explicit per-level value, for tests and for callers that already resolved
    /// the setting.</summary>
    public static int PassivePoints(int level, int pointsPerLevel) =>
        Math.Max(0, Math.Clamp(level, 1, MaxLevel) - 1) * Math.Max(0, pointsPerLevel);
}

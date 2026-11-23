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
    /// The monster level the experience penalty uses: above 70 a monster counts as a lower level,
    /// -0.03 * level² + 5.17 * level - 144.9 (the wiki's Experience page; 75 → 74.10, 82 → 77.32). It changes the
    /// penalty only, not the monster's stats, loot or base experience.
    /// </summary>
    public static double EffectiveMonsterLevel(int monsterLevel) => monsterLevel > 70
        ? -0.03 * monsterLevel * monsterLevel + 5.17 * monsterLevel - 144.9
        : monsterLevel;

    /// <summary>
    /// The level penalty (the wiki's Experience page): monsters within a safe zone of 3 + level / 16 levels give full
    /// experience; beyond it the experience is scaled by ((L + 5) / (L + 5 + d^2.5))^1.5, d the levels past the safe
    /// zone (L the player's level), at least 0.01. From level 95 another 1 / (1 + 0.1 * (L - 94)) applies, even in the
    /// safe zone. Monsters above level 70 use <see cref="EffectiveMonsterLevel"/> for d.
    /// </summary>
    public static double LevelPenalty(int playerLevel, int monsterLevel)
    {
        int safeZone = 3 + playerLevel / 16;
        double d = Math.Max(Math.Abs(playerLevel - EffectiveMonsterLevel(monsterLevel)) - safeZone, 0);
        double l = playerLevel + 5;
        double penalty = Math.Pow(l / (l + Math.Pow(d, 2.5)), 1.5);
        if (playerLevel >= 95) penalty /= 1 + 0.1 * (playerLevel - 94);
        return Math.Max(0.01, penalty);
    }

    /// <summary>
    /// How much more experience a monster of this rarity gives than a normal one, from the hidden rarity mods in the
    /// client's Mods table (<c>monster_slain_experience_+%</c>): magic 250% (×3.5), rare 750% (×8.5), unique 450% (×5.5).
    /// </summary>
    public static double RarityMultiplier(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 3.5,
        MonsterRarity.Rare => 8.5,
        MonsterRarity.Unique => 5.5,
        _ => 1,
    };

    /// <summary>
    /// The party's share for one player (the wiki's Experience page): a monster's experience is multiplied by the
    /// party size and split by (level + 10)^2.71, capped at the player's solo experience, so min{1, N * weight / Σ weights}.
    /// </summary>
    public static double PartyMultiplier(IReadOnlyList<int> levels, int index)
    {
        if (levels.Count <= 1) return 1;
        double total = 0;
        foreach (int level in levels) total += Math.Pow(level + 10, 2.71);
        if (total <= 0) return 1;
        return Math.Min(1, levels.Count * Math.Pow(levels[index] + 10, 2.71) / total);
    }

    /// <summary>
    /// The experience a kill gives: DefaultMonsterStats' Experience at the monster's level × the variety's
    /// ExperienceMultiplier (percent) × the rarity multiplier × the level penalty × the party share, rounded, at least
    /// 1 when the base is. With <paramref name="levelPenalty"/> false the penalty is skipped: the gems' 10% share is
    /// taken before it.
    /// </summary>
    public static uint MonsterExperience(int baseExperience, int multiplierPercent, MonsterRarity rarity, int playerLevel,
        int monsterLevel, bool levelPenalty = true, double partyMultiplier = 1)
    {
        if (baseExperience <= 0 || multiplierPercent <= 0) return 0;
        double experienceMultiplier = ExperienceMultiplier;
        if (experienceMultiplier <= 0) return 0;
        double xp = baseExperience * (multiplierPercent / 100.0) * RarityMultiplier(rarity)
            * (levelPenalty ? LevelPenalty(playerLevel, monsterLevel) : 1) * partyMultiplier * experienceMultiplier;
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

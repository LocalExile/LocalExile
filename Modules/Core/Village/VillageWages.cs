namespace GameServer.Modules.Village;

/// <summary>
/// What the town pays a worker every hour.
/// <para>
/// The client never works the wage out. The panel shows the number the server puts in the worker's row (the u16 at
/// <c>+0xa0</c>) and adds the rows up for its "Wage: n/h" line, so the wage is server data, like the worker's level (the
/// u8 skill byte, a <c>VillageJobSkillLevels</c> row).
/// </para>
/// <para>
/// The numbers are the community's Kingsmarch table: a worker's wage is a roll, set by the specialisation that pays the
/// most, and the table below is the "good roll" value players aim for when they reroll. The client's own
/// <c>VillageRecruitBalancePerTier</c> holds the 2,000 gold a reroll costs, the same figure the panel shows in its
/// "Reroll Recruits" tooltip.
/// </para>
/// </summary>
public static class VillageWages
{
    /// <summary>The job types a worker can hold (the client's <c>VillageJobTypes</c> rows).</summary>
    public const byte Idling = 0, Mining = 1, Smelting = 2, Farming = 3, Disenchanting = 4, Mapping = 5, Shipping = 6,
        Fishing = 7;

    /// <summary>How many ranks a worker can have (the client's <c>VillageJobSkillLevels</c> rows, job level 0 to 5).</summary>
    public const int Ranks = 5;

    /// <summary>The hourly wage by job type; each row is the rank 1 to rank 5 wage in gold.</summary>
    static readonly ushort[][] Wages =
    [
        [0, 0, 0, 0, 0],                      // Idling: a worker without a job costs nothing
        [80, 120, 160, 220, 280],              // Mining
        [80, 120, 160, 220, 280],              // Smelting
        [190, 285, 391, 517, 665],             // Farming
        [170, 255, 349, 463, 595],             // Disenchanting
        [375, 560, 750, 1100, 1875],           // Mapping
        [110, 165, 225, 290, 300],             // Shipping
        [0, 0, 0, 0, 0],                      // Fishing
    ];

    /// <summary>The skill byte a worker of the given rank carries: the <c>VillageJobSkillLevels</c> row the client looks
    /// the rank up by (<c>0x1424c2af0</c>), whose <c>JobLevel</c> column is the rank itself (1 to 5). The table's row 0 is
    /// "no rank", which is also the byte a worker with no specialisation would carry.</summary>
    /// <param name="rank">The rank, 1 to 5.</param>
    public static byte Skill(int rank) => (byte)Math.Clamp(rank, 1, Ranks);

    /// <summary>The rank a skill byte stands for, 1 to 5.</summary>
    /// <param name="skill">The worker's skill level (the client's job level).</param>
    public static int Rank(byte skill) => Math.Clamp((int)skill, 1, Ranks);

    /// <summary>What a worker of this job and rank costs the town every hour.</summary>
    /// <param name="jobType">The worker's job (a <c>VillageJobTypes</c> row).</param>
    /// <param name="skill">The worker's job level (the skill byte the row carries, 1 to 5).</param>
    public static ushort Wage(byte jobType, byte skill)
    {
        if (jobType >= Wages.Length) return 0;
        var row = Wages[jobType];
        // the table is per rank, 1 to 5; the level-0 byte a worker with no rank carries is paid as the first rank
        return row[Math.Clamp((int)skill - 1, 0, Ranks - 1)];
    }
}

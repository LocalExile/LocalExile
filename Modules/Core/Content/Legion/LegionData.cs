using GameServer.Core;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Legion client.db tables, read once per module load (LegionFactions, LegionRanks, LegionMonsterVarieties,
/// LegionMonsterCounts, LegionChestTypes, LegionChestCounts, LegionBalancePerLevel). Columns whose meaning is not
/// known stay raw ints named by their column number.
/// </summary>
public sealed class LegionData
{
    /// <summary>A LegionFactions row: schema columns plus col3 formation orderliness (0.95 orderly grid, 0.65/0.50 loose).</summary>
    public sealed record Faction(int Row, string Id, int SpawnWeight, int BalanceKey, int StatsKey, int BuffVisualsKey, int[] MiscAnimated, double Orderliness = 0.5);

    /// <summary>A LegionRanks row; col0 is tier (0..3), col1..col4 rarity weights, col7 general/hoard chance, col8 alert value (phase 4).</summary>
    public sealed record Rank(int Row, int Col0, int Col1, int Col2, int Col3, int Col4, int BalanceKey, int Col6, int Col7, int Col8);

    /// <summary>A LegionMonsterVarieties row; col3 meaning unknown, col5 prop chance (%), col6 variety spawn weight,
    /// col10/col11 prop clip time range (ms), col15 prop count per MiscAnimatedKey entry.</summary>
    public sealed record MonsterVariety(int Row, int Monster, int Faction, int RankKey, int Col3, int[] MiscAnimated, int PropChance = 0, int SpawnWeight = 1, int[]? PropFromMs = null, int[]? PropToMs = null, int[]? PropCounts = null);

    /// <summary>A LegionMonsterCounts row; cols 2-7 meaning unknown.</summary>
    public sealed record MonsterCount(int Row, int Faction, int RankKey, int Col2, int Col3, int Col4, int Col5, int Col6, int Col7);

    /// <summary>A LegionChestTypes row; col0 is the frozen chest monster variety, Chest/HardmodeChest are the Chests rows it becomes at release, col3 meaning unknown.</summary>
    public sealed record ChestType(int Row, int Col0, int Chest, int Col3, int Faction, int HardmodeChest = -1);

    /// <summary>A LegionChestCounts row; cols 2-5 and 7 meaning unknown.</summary>
    public sealed record ChestCount(int Row, int Faction, int RankKey, int MinLevel, int Col2, int Col3, int Col4, int Col5, int Col7);

    /// <summary>A LegionBalancePerLevel row from MinLevel; Cols holds col1..col15, meaning unknown except col3.</summary>
    public sealed record Balance(int Row, int MinLevel, int[] Cols)
    {
        /// <summary>col3: timer length in ms (client 0x1416290a0).</summary>
        public int DurationMs => Cols[2];
    }

    /// <summary>A LegionRewardTypeVisuals row: its minimap icon (MinimapIcons row; the monster stat is the row + 1). The client draws the symbol by row.</summary>
    public sealed record RewardType(int Row, int MinimapIcon);

    public IReadOnlyList<RewardType> RewardTypes { get; }
    public IReadOnlyList<Faction> Factions { get; }
    public IReadOnlyList<Rank> Ranks { get; }
    public IReadOnlyList<MonsterVariety> MonsterVarieties { get; }
    public IReadOnlyList<MonsterCount> MonsterCounts { get; }
    public IReadOnlyList<ChestType> ChestTypes { get; }
    public IReadOnlyList<ChestCount> ChestCounts { get; }
    public IReadOnlyList<Balance> Balances { get; }

    static LegionData? Loaded;
    public static LegionData Current => Loaded ??= new LegionData();
    public static LegionData Reload() => Loaded = new LegionData();
    public static void Unload() => Loaded = null;

    /// <summary>An optional column (the prop columns are a guess) that is a list or a number: its numbers, or none when it is neither, so a type surprise
    /// skips the props instead of failing the module.</summary>
    static int[] Tolerant(ClientRow r, string column)
    {
        try { return r.Ints(column); }
        catch (Exception) { }
        try { return r.IntOrNull(column) is int v ? [v] : []; }
        catch (Exception) { return []; }
    }

    LegionData()
    {
        string[] animated = ["MiscAnimatedKey1", "MiscAnimatedKey2", "MiscAnimatedKey3", "MiscAnimatedKey4", "MiscAnimatedKey5"];
        Factions = [.. ClientTables.TableOrEmpty("LegionFactions").Select(r => new Faction(r.Index, r.Text("Id") ?? "", r.Int("SpawnWeight"),
            r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.IntOrNull("StatsKey") ?? -1, r.IntOrNull("BuffVisualsKey") ?? -1,
            [.. animated.Select(c => r.IntOrNull(c) ?? -1)], r.RealOrNull("__col3") ?? 0.5))];
        Ranks = [.. ClientTables.TableOrEmpty("LegionRanks").Select(r => new Rank(r.Index, r.Int("__col0"), r.Int("__col1"), r.Int("__col2"),
            r.Int("__col3"), r.Int("__col4"), r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.Int("__col6"), r.Int("__col7"), r.Int("__col8")))];
        MonsterVarieties = [.. ClientTables.TableOrEmpty("LegionMonsterVarieties").Select(r =>
        {
            int rank = r.IntOrNull("LegionRanksKey") ?? -1;
            int weight = Tolerant(r, "__col6").FirstOrDefault();
            return new MonsterVariety(r.Index, r.IntOrNull("MonsterVarietiesKey") ?? -1, r.IntOrNull("LegionFactionsKey") ?? -1, rank,
                r.Int("__col3"), r.Ints("MiscAnimatedKey"), Tolerant(r, "__col5").FirstOrDefault(), weight > 0 ? weight : 1,
                Tolerant(r, "__col10"), Tolerant(r, "__col11"), Tolerant(r, "__col15"));
        })];
        MonsterCounts = [.. ClientTables.TableOrEmpty("LegionMonsterCounts").Select(r => new MonsterCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("__col2"), r.Int("__col3"), r.Int("__col4"),
            r.Int("__col5"), r.Int("__col6"), r.Int("__col7")))];
        ChestTypes = [.. ClientTables.TableOrEmpty("LegionChestTypes").Select(r => new ChestType(r.Index, r.IntOrNull("__col0") ?? -1,
            r.IntOrNull("Chest") ?? -1, r.Int("__col3"), r.IntOrNull("Faction") ?? -1, r.IntOrNull("HardmodeChest") ?? -1))];
        ChestCounts = [.. ClientTables.TableOrEmpty("LegionChestCounts").Select(r => new ChestCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("MinLevel"), r.Int("__col2"), r.Int("__col3"),
            r.Int("__col4"), r.Int("__col5"), r.Int("__col7")))];
        RewardTypes = [.. ClientTables.TableOrEmpty("LegionRewardTypeVisuals").Select(r => new RewardType(r.Index, r.IntOrNull("MinimapIconsKey") ?? -1))];
        Balances = [.. ClientTables.TableOrEmpty("LegionBalancePerLevel").Select(r => new Balance(r.Index, r.Int("MinLevel"),
            [.. Enumerable.Range(1, 15).Select(c => r.Int($"__col{c}"))]))];
    }
}

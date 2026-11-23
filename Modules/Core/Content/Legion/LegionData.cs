using GameServer.Core;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Legion client.db tables, read once per module load (LegionFactions, LegionRanks, LegionMonsterVarieties,
/// LegionMonsterCounts, LegionChestTypes, LegionChestCounts, LegionBalancePerLevel). Columns whose meaning is not
/// known stay raw ints named by their column number.
/// </summary>
public sealed class LegionData
{
    /// <summary>A LegionFactions row: the schema-named columns only; col3, col4, col12 and col13 are not read.</summary>
    public sealed record Faction(int Row, string Id, int SpawnWeight, int BalanceKey, int StatsKey, int BuffVisualsKey, int[] MiscAnimated);

    /// <summary>A LegionRanks row; col0-col4 and col6-col8 meaning unknown, the balance key is named by the schema.</summary>
    public sealed record Rank(int Row, int Col0, int Col1, int Col2, int Col3, int Col4, int BalanceKey, int Col6, int Col7, int Col8);

    /// <summary>A LegionMonsterVarieties row; col3 meaning unknown.</summary>
    public sealed record MonsterVariety(int Row, int Monster, int Faction, int RankKey, int Col3, int[] MiscAnimated);

    /// <summary>A LegionMonsterCounts row; cols 2-7 meaning unknown.</summary>
    public sealed record MonsterCount(int Row, int Faction, int RankKey, int Col2, int Col3, int Col4, int Col5, int Col6, int Col7);

    /// <summary>A LegionChestTypes row; col0 and col3 meaning unknown.</summary>
    public sealed record ChestType(int Row, int Col0, int Chest, int HardmodeChest, int Col3, int Faction);

    /// <summary>A LegionChestCounts row; cols 2-5 and 7 meaning unknown.</summary>
    public sealed record ChestCount(int Row, int Faction, int RankKey, int MinLevel, int Col2, int Col3, int Col4, int Col5, int Col7);

    /// <summary>A LegionBalancePerLevel row from MinLevel; Cols holds col1..col15, meaning unknown.</summary>
    public sealed record Balance(int Row, int MinLevel, int[] Cols);

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

    LegionData()
    {
        string[] animated = ["MiscAnimatedKey1", "MiscAnimatedKey2", "MiscAnimatedKey3", "MiscAnimatedKey4", "MiscAnimatedKey5"];
        Factions = [.. ClientTables.TableOrEmpty("LegionFactions").Select(r => new Faction(r.Index, r.Text("Id") ?? "", r.Int("SpawnWeight"),
            r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.IntOrNull("StatsKey") ?? -1, r.IntOrNull("BuffVisualsKey") ?? -1,
            [.. animated.Select(c => r.IntOrNull(c) ?? -1)]))];
        Ranks = [.. ClientTables.TableOrEmpty("LegionRanks").Select(r => new Rank(r.Index, r.Int("__col0"), r.Int("__col1"), r.Int("__col2"),
            r.Int("__col3"), r.Int("__col4"), r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.Int("__col6"), r.Int("__col7"), r.Int("__col8")))];
        MonsterVarieties = [.. ClientTables.TableOrEmpty("LegionMonsterVarieties").Select(r =>
        {
            int rank = r.IntOrNull("LegionRanksKey") ?? -1;
            return new MonsterVariety(r.Index, r.IntOrNull("MonsterVarietiesKey") ?? -1, r.IntOrNull("LegionFactionsKey") ?? -1, rank,
                r.Int("__col3"), r.Ints("MiscAnimatedKey"));
        })];
        MonsterCounts = [.. ClientTables.TableOrEmpty("LegionMonsterCounts").Select(r => new MonsterCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("__col2"), r.Int("__col3"), r.Int("__col4"),
            r.Int("__col5"), r.Int("__col6"), r.Int("__col7")))];
        ChestTypes = [.. ClientTables.TableOrEmpty("LegionChestTypes").Select(r => new ChestType(r.Index, r.IntOrNull("__col0") ?? -1,
            r.IntOrNull("Chest") ?? -1, r.IntOrNull("HardmodeChest") ?? -1, r.Int("__col3"), r.IntOrNull("Faction") ?? -1))];
        ChestCounts = [.. ClientTables.TableOrEmpty("LegionChestCounts").Select(r => new ChestCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("MinLevel"), r.Int("__col2"), r.Int("__col3"),
            r.Int("__col4"), r.Int("__col5"), r.Int("__col7")))];
        Balances = [.. ClientTables.TableOrEmpty("LegionBalancePerLevel").Select(r => new Balance(r.Index, r.Int("MinLevel"),
            [.. Enumerable.Range(1, 15).Select(c => r.Int($"__col{c}"))]))];
    }
}

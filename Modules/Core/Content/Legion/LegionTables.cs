using GameServer.Core;

namespace GameServer.Modules.Legion;

/// <summary>
/// The numbers of the Legion encounter. What the client tables name is read from them in <see cref="LoadGameData"/>
/// (LegionFactions, LegionBalancePerLevel, LegionRewardTypeVisuals with MiscAnimated, Environments, Rulesets); the
/// rest is the tables' unnamed columns read by their values at level 68 (the level of the official captures), and
/// the geometry of the official captures. A row for an area level is the one with the largest MinLevel not above it,
/// or the level-68 row when none qualifies; each dependent table is read at its own largest level not above the area's,
/// because not every balance key has rows in every table.
/// </summary>
public static class LegionTables
{
    /// <summary>LegionFactions: the id and SpawnWeight of each faction (Faridun's is 0).</summary>
    public static IReadOnlyList<(string Id, int Weight)> Factions { get; private set; } = [];

    /// <summary>LegionBalancePerLevel: MinLevel and the freeze timer in milliseconds (the fourth column).</summary>
    static (int MinLevel, int FreezeMs)[] balance = [];

    /// <summary>The Rulesets row of LegionRules, or null without the tables.</summary>
    public static byte? RulesetRow { get; private set; }

    /// <summary>The Environments rows of LegionLeagueWorld, ...WorldBattle and ...WorldPostBattle.</summary>
    public static ushort EnvWorld { get; private set; }
    public static ushort EnvBattle { get; private set; }
    public static ushort EnvPostBattle { get; private set; }

    /// <summary>LegionRewardTypeVisuals: per reward type its minimap icon row, the .ao of its MiscAnimated row and its scale.</summary>
    public static IReadOnlyList<(string Id, int MinimapIcon, string Ao, double Scale)> RewardSymbols { get; private set; } = [];

    public static void LoadGameData()
    {
        Factions = [.. ClientTables.TableOrEmpty("LegionFactions")
            .Select(r => (r.Text("Id") ?? "", r.IntOrNull("SpawnWeight") ?? 0))];
        balance = [.. ClientTables.TableOrEmpty("LegionBalancePerLevel")
            .Select(r => (r.IntOrNull("MinLevel") ?? 0, r.IntOrNull("__col3") ?? 0)).OrderBy(r => r.Item1)];
        RulesetRow = RowOf("Rulesets", "LegionRules") is int rules and <= byte.MaxValue ? (byte)rules : null;
        EnvWorld = EnvironmentRow("LegionLeagueWorld");
        EnvBattle = EnvironmentRow("LegionLeagueWorldBattle");
        EnvPostBattle = EnvironmentRow("LegionLeagueWorldPostBattle");
        var animated = ClientTables.TableOrEmpty("MiscAnimated");
        RewardSymbols = [.. ClientTables.TableOrEmpty("LegionRewardTypeVisuals").Select(r =>
            (r.Text("Id") ?? "", r.IntOrNull("MinimapIconsKey") ?? 0,
                animated.Row(r.IntOrNull("MiscAnimatedKey") ?? -1)?.Text("AOFile") ?? "", r.RealOrNull("__col4") ?? 3.0))];

        // LegionMonsterVarieties: the MonsterVarieties row of each (faction, rank 0..3); the other rank key (57) is not an army's.
        var pool = new Dictionary<(string, Rank), List<int>>();
        foreach (var r in ClientTables.TableOrEmpty("LegionMonsterVarieties"))
            if (r.IntOrNull("MonsterVarietiesKey") is int variety && r.IntOrNull("LegionFactionsKey") is int f && (uint)f < (uint)Factions.Count
                && r.IntOrNull("LegionRanksKey") is int rank and >= 0 and <= (int)Rank.General)
            {
                if (!pool.TryGetValue((Factions[f].Id, (Rank)rank), out var rows)) pool[(Factions[f].Id, (Rank)rank)] = rows = [];
                rows.Add(variety);
            }
        varietyPool = pool.ToDictionary(e => e.Key, e => e.Value.ToArray());

        // LegionChestTypes: per faction the hoard's MonsterChest variety (the first column) and the object path its Chests row
        // names; the War Hoard's (epic) path ends in "Epic".
        var chests = ClientTables.TableOrEmpty("Chests");
        var normal = new Dictionary<string, (int, string)>();
        var epic = new Dictionary<string, (int, string)>();
        foreach (var r in ClientTables.TableOrEmpty("LegionChestTypes"))
            if (r.IntOrNull("Faction") is int f && (uint)f < (uint)Factions.Count && r.IntOrNull("__col0") is int monster
                && chests.Row(r.IntOrNull("Chest") ?? -1)?.Text("Id") is { Length: > 0 } path)
                (path.EndsWith("Epic", StringComparison.Ordinal) ? epic : normal)[Factions[f].Id] = (monster, path);
        chestNormal = normal;
        chestEpic = epic;
    }

    static Dictionary<(string Faction, Rank Rank), int[]> varietyPool = [];
    static Dictionary<string, (int MonsterRow, string ChestPath)> chestNormal = [], chestEpic = [];

    static int? RowOf(string table, string id) =>
        ClientTables.TableOrEmpty(table).FirstOrDefault(r => r.Text("Id") == id) is { Table: not null } row ? row.Index : null;

    static ushort EnvironmentRow(string id) => RowOf("Environments", id) is int row and <= ushort.MaxValue ? (ushort)row : (ushort)0;

    /// <summary>The row with the largest level not above <paramref name="level"/>, or the level-68 row when none is.</summary>
    static T RowFor<T>(T[] rows, Func<T, int> levelOf, int level) where T : struct
    {
        T? found = null;
        foreach (var row in rows)
            if (levelOf(row) <= level)
                found = row;
        return found ?? rows.First(r => levelOf(r) == 68);
    }

    /// <summary>The freeze timer (ms) for this area level: 15 000 from level 68 up, 30 000 at level 1.</summary>
    public static int FreezeMs(int level) => balance.Length == 0 ? 15000 : RowFor(balance, r => r.MinLevel, level).FreezeMs;

    public enum Rank { Soldier = 0, Elite = 1, Sergeant = 2, General = 3 }

    // LegionRanks: per balance key (the MinLevel of its LegionBalancePerLevel row) the rarity weights of ranks 0..3, as the
    // columns normal / magic / rare / unique, and the General's chance in percent (the rank-3 row's last column).
    // The rarity is the same for every key but 101, where rank 0 reads (75, 25, 0, 0).
    static readonly (int MinLevel, (int Normal, int Magic, int Rare, int Unique) R0,
        (int Normal, int Magic, int Rare, int Unique) R1,
        (int Normal, int Magic, int Rare, int Unique) R2,
        (int Normal, int Magic, int Rare, int Unique) R3, double GeneralChancePct)[] RanksByLevel =
    [
        (1, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (6, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (10, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (15, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (20, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (30, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (41, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
        (46, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 1),
        (60, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 1),
        (65, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 1),
        (68, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 5),
        (73, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 6),
        (78, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 7),
        (82, (90, 10, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 8),
        (101, (75, 25, 0, 0), (75, 25, 0, 0), (0, 0, 100, 0), (0, 0, 0, 100), 0),
    ];

    /// <summary>The rarity weights (normal, magic, rare, unique) of the four ranks for this area level.</summary>
    public static (int Normal, int Magic, int Rare, int Unique)[] RarityWeights(int level)
    {
        var row = RowFor(RanksByLevel, r => r.MinLevel, level);
        return [row.R0, row.R1, row.R2, row.R3];
    }

    /// <summary>The chance (%) of one General per encounter for this area level.</summary>
    public static double GeneralChancePct(int level) => RowFor(RanksByLevel, r => r.MinLevel, level).GeneralChancePct;

    // LegionMonsterCounts: the count range and the annulus (percent of the radius) per rank, by the row's level. At level
    // 101 the rank-0 and rank-1 counts are fixed per faction (CountsLevel101).
    static readonly (int MinLevel, (int Min, int Max) C0, (int Min, int Max) C1,
        (int Min, int Max) C2, (int Min, int Max) C3, (double Lo, double Hi) A0,
        (double Lo, double Hi) A1, (double Lo, double Hi) A2, (double Lo, double Hi) A3)[] MonsterCountsByLevel =
    [
        (1, (50, 60), (20, 30), (0, 0), (0, 0), (0, 95), (20, 95), (40, 100), (90, 100)),
        (6, (50, 60), (20, 30), (0, 2), (0, 0), (0, 95), (20, 95), (40, 100), (90, 100)),
        (10, (50, 60), (20, 30), (1, 3), (0, 0), (0, 95), (20, 95), (40, 100), (90, 100)),
        (20, (50, 60), (20, 30), (2, 3), (0, 0), (0, 95), (20, 95), (40, 100), (90, 100)),
        (41, (50, 60), (20, 30), (3, 4), (0, 0), (0, 95), (20, 95), (40, 100), (90, 100)),
        (46, (50, 60), (20, 30), (3, 4), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (60, (60, 72), (24, 36), (3, 4), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (65, (60, 72), (24, 36), (3, 4), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (68, (60, 72), (24, 36), (3, 5), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (73, (60, 72), (24, 36), (3, 5), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (78, (60, 72), (24, 36), (3, 5), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (82, (60, 72), (24, 36), (3, 5), (1, 1), (0, 95), (20, 95), (40, 100), (90, 100)),
        (101, (24, 24), (65, 65), (4, 4), (1, 1), (0, 35), (36, 95), (40, 100), (90, 100)),
    ];

    static readonly Dictionary<(string Faction, Rank Rank), (int Min, int Max)> CountsLevel101 = new()
    {
        [("Karui", Rank.Soldier)] = (24, 24),
        [("Vaal", Rank.Soldier)] = (28, 28),
        [("EternalEmpire", Rank.Soldier)] = (24, 24),
        [("Maraketh", Rank.Soldier)] = (36, 36),
        [("Templar", Rank.Soldier)] = (32, 32),
        [("Faridun", Rank.Soldier)] = (36, 36),
        [("Karui", Rank.Elite)] = (65, 65),
        [("Vaal", Rank.Elite)] = (70, 70),
        [("EternalEmpire", Rank.Elite)] = (65, 65),
        [("Maraketh", Rank.Elite)] = (80, 80),
        [("Templar", Rank.Elite)] = (75, 75),
        [("Faridun", Rank.Elite)] = (80, 80),
    };

    // The rank-2 annulus starts at 50, not 40, for these (faction, level) rows only.
    static readonly (string Faction, int MinLevel)[] AnnulusLo50 = [("Karui", 20), ("Templar", 60), ("Karui", 78)];

    /// <summary>How many units of this faction and rank an encounter plans, uniform in [min, max].</summary>
    public static (int Min, int Max) Counts(string faction, Rank rank, int level)
    {
        var row = RowFor(MonsterCountsByLevel, r => r.MinLevel, level);
        if (row.MinLevel == 101 && rank is Rank.Soldier or Rank.Elite && CountsLevel101.TryGetValue((faction, rank), out var fixedCount))
            return fixedCount;
        return rank switch { Rank.Soldier => row.C0, Rank.Elite => row.C1, Rank.Sergeant => row.C2, _ => row.C3 };
    }

    /// <summary>The annulus a rank stands in, as percent of the radius, for this faction and area level.</summary>
    public static (double Lo, double Hi) AnnulusPct(string faction, Rank rank, int level)
    {
        var row = RowFor(MonsterCountsByLevel, r => r.MinLevel, level);
        var annulus = rank switch { Rank.Soldier => row.A0, Rank.Elite => row.A1, Rank.Sergeant => row.A2, _ => row.A3 };
        return rank == Rank.Sergeant && AnnulusLo50.Contains((faction, row.MinLevel)) ? (50, annulus.Hi) : annulus;
    }

    // LegionChestCounts: 0..2 chests from the rank-0 row and the War Hoard chance from the rank-1 row, by MinLevel. The
    // chest annuli read (20, 70) and (35, 100) at every level. At level 78 the War Hoard chance is 20 for Templar.
    static readonly (int MinLevel, int ChestMin, int ChestMax, double WarHoardChancePct)[] ChestCountsByLevel =
    [
        (1, 0, 2, 0), (6, 0, 2, 0), (10, 0, 2, 0), (15, 0, 2, 0), (20, 0, 2, 0),
        (30, 0, 2, 0), (41, 0, 2, 5), (46, 0, 2, 5), (60, 0, 2, 0), (65, 0, 2, 0),
        (68, 0, 2, 13), (73, 0, 2, 16), (78, 0, 2, 16), (82, 0, 2, 20),
    ];
    public static readonly (double Lo, double Hi)[] ChestAnnulusPct = [(20, 70), (35, 100)];

    public static (int Min, int Max) ChestRange(int level)
    {
        var row = RowFor(ChestCountsByLevel, r => r.MinLevel, level);
        return (row.ChestMin, row.ChestMax);
    }

    public static double WarHoardChancePct(string faction, int level)
    {
        var row = RowFor(ChestCountsByLevel, r => r.MinLevel, level);
        return row.MinLevel == 78 && faction == "Templar" ? 20 : row.WarHoardChancePct;
    }

    /// <summary>The balance radius in cells: 200 in every LegionBalancePerLevel row.</summary>
    public const double Radius = 200;

    // Official captures: the units stand on a lattice, 12 cells along a row, rows 10 apart, every other row shifted 6, and
    // none stands nearer the monolith than 30 cells.
    public const double LatticeAlong = 12;
    public const double LatticeRow = 10;
    public const double LatticeShift = 6;
    public const double Clear = 30;

    /// <summary>The monolith and the army show once the player is this near (the captures create them at 370 to 494 cells).</summary>
    public const double Reveal = 400;

    /// <summary>How near a click on the monolith walks up to it (cells): its LegionInitiator.ot object_size.</summary>
    public const int ClickReach = 4;

    /// <summary>The smallest distance from the arrival to a monolith over the 9 official Legion instances captured is 422 cells.</summary>
    public const double EntryDist = 2 * Radius;

    // Official captures: 45 frames from the click to the activation, 16 frames from the freeze to the battle state, at 30
    // frames a second; the fight ended 85 to 213 frames after the last released unit died.
    public const double ChargeSeconds = 45.0 / 30;
    public const double BattleAfterFreezeSeconds = 16.0 / 30;
    public const double EndDelaySeconds = 6.5;
    public const double BattleMaxSeconds = 120;

    /// <summary>The monolith's crystal state: one to three, by the number of hoards.</summary>
    public static int Crystal(int hoards) => Math.Min(3, Math.Max(1, hoards));

    /// <summary>The symbol's scale when the table names none.</summary>
    public const double SymbolScale = 3.0;

    /// <summary>The LegionRewardTypeVisuals row for this reward type (the first of duplicated ids), or null.</summary>
    public static (string Id, int MinimapIcon, string Ao, double Scale)? RewardSymbol(string id)
    {
        foreach (var row in RewardSymbols)
            if (row.Id == id) return row;
        return null;
    }

    /// <summary>The MonsterVarieties rows a unit of this faction and rank is drawn from (uniform); empty when none. A variety whose
    /// id contains "Endless" is skipped when the army is created.</summary>
    public static int[] VarietyRows(string faction, Rank rank) => varietyPool.GetValueOrDefault((faction, rank), []);

    /// <summary>A hoard's monster variety row and chest object path for this faction (<c>epic</c>: the War Hoard's), or null.</summary>
    public static (int MonsterRow, string ChestPath)? ChestType(string faction, bool epic) =>
        (epic ? chestEpic : chestNormal).TryGetValue(faction, out var chest) ? chest : null;

    public const string MonolithPath = "Metadata/Terrain/Leagues/Legion/Objects/LegionInitiator";
    public const string FlashLightPath = "Metadata/Terrain/Leagues/Legion/Objects/LegionFlashLight";
    public const string SymbolPath = "Metadata/Effects/SleepableServerEffect";
}

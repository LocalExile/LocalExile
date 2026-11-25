using GameServer.Core;

namespace GameServer.Modules.Abyss;

/// <summary>
/// The Abyss encounter's numbers, each with where it comes from: [data] is a client table, [capture] is measured on the two
/// official Abyss captures (a level 61 and a level 45 area, 72 crack creates between them), [ESTIMATE] is a value neither
/// of them shows. What the client tables name is read in <see cref="LoadGameData"/>. Table rows are looked up by Id in the
/// client tables; no row number of a capture is sent.
/// </summary>
public static class AbyssTables
{
    /// <summary>The cracks, the pit and the plinth [data: object types].</summary>
    public const string CrackType = "Metadata/MiscellaneousObjects/Abyss/AbyssCrack";
    public const string PitType = "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase";
    public const string PlinthType = "Metadata/MiscellaneousObjects/Abyss/AbyssPlinth";

    /// <summary>The plinth's art, a closed platform carrying the chest [capture 1/1].</summary>
    public const string PlinthAo = "Metadata/Effects/Environment/League_AbyssTwo/AbyssCirclePlatform/AbyssCirclePlatformClosedChest_01.ao";

    /// <summary>The reward chest the level 45 capture created at the pit's cell (a Chests row, "Greater Abyssal Trove"
    /// [capture 1/1, data]). The Chests table has 14 more <c>LeagueAbyss/FinalChest*</c> rows (Magic, Armour, Currency,
    /// Weapon...) whose choice no capture shows, so only this one is made [ESTIMATE: that every Abyss gives this one]. Its
    /// own .ot is not among the extracted object types: the row's InheritsFrom (<see cref="ChestParentType"/>) gives the
    /// components, the same list the captured create has.</summary>
    public const string RewardChest = "Metadata/Chests/LeagueAbyss/FinalChestRareJewellery";
    public const string ChestParentType = "Metadata/Chests/Abyss/AbyssFinalChestTrove";

    /// <summary>The plinth's two properties, both carrying the pit's entity id [capture 1/1].</summary>
    public static readonly uint[] PitPropertyKeys = [0x40f0fc9c, 0x7700c879];

    /// <summary>The crack's Positioned object_size is 3: a piece's cell needs clearance 3, size 2 for
    /// <c>AreaLayout.WalkableFor</c> (object_size − 1).</summary>
    public const int CrackSize = 2;

    /// <summary>The lattice: 125 world units between two pieces; a cell is 250/23 world units [capture 72/72].</summary>
    public const double Lattice = 125.0, Cell = 250.0 / 23.0;

    /// <summary>Up to 3 arms [data: MiscAnimated has the AbyssPit One / Two / Three shapes, no Four]; 13-16 pieces each
    /// [capture 13 13 14 16 16]; an arm shorter than 8 is dropped [ESTIMATE].</summary>
    public const int MaxArms = 3, ArmLenMin = 13, ArmLenMax = 16, ArmMin = 8;

    /// <summary>How far, in lattice steps, the pit may stand from the area's encounter spot: the plan keeps the point
    /// around it with the longest arms [ESTIMATE].</summary>
    public const int SiteRadius = 3;

    /// <summary>Each piece is created when a player comes this close to it, in cells [capture: the level 45 area's 46
    /// cracks and its pit were created one by one as the player walked up, at 241 to 283 cells (median 253) from the
    /// player's last known cell].</summary>
    public const double RevealDistance = 250;

    /// <summary>An arm starts closing when a monster dies this close to one of its pieces [capture: in the level 45 area the
    /// first arm closed after kills of monsters created 62, 75 and 85 cells from a piece, and a kill of one created 91
    /// away started none; the death cells are not in the capture, so 85 is an [ESTIMATE] inside that bracket], or when
    /// another arm is closing and the player is this close to it [ESTIMATE: the three arms there started within 115
    /// records of one another with the player 119 to 205 cells from their far ends; the capture has no clock to say
    /// more]. Walking on a crack started nothing [capture].</summary>
    public const double InfluenceDistance = 85, ArmNearDistance = 120;

    /// <summary>25 arm monsters per encounter [capture 25 and 25], 3-8 cells from their piece [capture 3.2 to 8.1].</summary>
    public const int ArmMonsters = 25;
    public const double ArmMonsterDistanceMin = 3, ArmMonsterDistanceMax = 8;

    /// <summary>A wave of 10-12 [capture 10, 12], 13-26 cells from the pit [capture]; its first monster is the family's
    /// elite (rare) with chance 0.5 [capture 1 of 2].</summary>
    public const int WaveMin = 10, WaveMax = 12;
    public const double WaveDistanceMin = 13, WaveDistanceMax = 26;
    public const double EliteChance = 0.5;

    /// <summary>The reward chest's walk-up size when its type has no row in the object types: the strongbox's [ESTIMATE;
    /// the capture's player opened it from 7 cells away, which says nothing of the size].</summary>
    public const int ChestSizeFallback = 2;

    /// <summary>The captures count 30 frames to the second; the encounter keeps its times in seconds: a piece closes a
    /// step every 16 frames, the wave's first monster comes 18 frames after the pit opens and the next every 4, the chest
    /// is ready 91 frames after the wave [capture].</summary>
    public const double Frame = 1.0 / 30;
    public const double CloseStepSeconds = 16 * Frame, WaveFirstSeconds = 18 * Frame, WaveEverySeconds = 4 * Frame,
        ChestReadySeconds = 91 * Frame;

    /// <summary>Transitionable states [capture]: a crack is created open (8, with the 0x80 first-sight bit), closing
    /// 9 / 10 / 11; the pit is created closed (1), opens (2) and ends done (4); the chest is 2 once ready and 3 opened.</summary>
    public const byte CrackOpen = 8, CrackClosing1 = 9, CrackClosing2 = 10, CrackClosing3 = 11;
    public const byte PitClosed = 1, PitOpening = 2, PitDoneState = 4, ChestReadyState = 2, ChestOpenedState = 3;

    /// <summary>The crack shapes' MiscAnimated rows [data].</summary>
    public const string StraightOrthogonal = "AbyssStraightOrthogonal1", StraightDiagonal = "AbyssStraightDiagonal",
        Corner90Orthogonal = "AbyssCorner90Orthogonal", Corner90Diagonal = "AbyssCorner90Diagonal",
        Corner135 = "AbyssCorner135", Corner135Reverse = "AbyssCorner135Reverse",
        StartOrthogonal = "AbyssStartOrthogonal", StartDiagonal = "AbyssStartDiagonal";

    /// <summary>The pit shapes [data] and the arms they carry at rotation 0, as the 8 lattice directions (0 up,
    /// clockwise) [capture for TwoOrthogonal and ThreeOrthogonal, ESTIMATE for One and Two90].</summary>
    public static readonly (string Shape, int[] Arms)[] PitShapes =
        [("AbyssPitThreeOrthogonal", [0, 2, 4]), ("AbyssPitTwoOrthogonal", [0, 4]), ("AbyssPitTwo90", [0, 2]),
         ("AbyssPitOneOrthogonal", [0])];

    /// <summary>The families an encounter draws from: Pit and Lightless from the InvasionMonstersPerArea rows of the
    /// WorldAreas AbyssLeagueBoss and AbyssLeagueBoss2 (MonsterVarietiesKeys1 then Keys2, the last of Keys1 the elite)
    /// [data; the types the level 45 capture declared and created are the Pit family's, the level 61 capture's the
    /// Lightless family's]. A type the server cannot create is skipped at spawn.</summary>
    public static IReadOnlyList<(string Name, string[] Regular, string Elite)> Families { get; private set; } = [];

    /// <summary>The MinimapIcons row of AbyssChestActive (the pit's icon once the chest is there) [data], or -1.</summary>
    public static int ChestIconRow { get; private set; } = -1;

    /// <summary>The reward chest's item quantity, in percent more [data: its Chests row's mods, the stat
    /// <c>chest_item_quantity_+%</c> = 300, which makes 4 of the 1 it would drop; the capture's chest dropped 4], and
    /// whether the Chests table has the row.</summary>
    public static int ChestQuantityPct { get; private set; }

    /// <summary>The reward chest's item rarity, in percent increased [data: its Chests row's mods, the stat
    /// <c>chest_item_rarity_+%</c> = 25000, which is what makes the trove's items rare].</summary>
    public static int ChestRarityPct { get; private set; }
    public static bool HasRewardChest { get; private set; }

    /// <summary>The CoreLeagues row of Abyss (a bit of MapDisableLeagues), looked up by its Id.</summary>
    public static int LeagueRow { get; private set; } = LeagueChances.Abyss;

    static Dictionary<string, uint> animRows = new(StringComparer.Ordinal);

    /// <summary>The MiscAnimated row of a shape, or 0 when the table has none (the crack is then drawn with its type's own
    /// model).</summary>
    public static uint AnimRow(string shape) => animRows.GetValueOrDefault(shape);

    /// <summary>Reads the client tables (the module's LoadData).</summary>
    public static void LoadGameData()
    {
        var rows = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("MiscAnimated"))
            if (r.TextOrNull("Id") is { Length: > 0 } name) rows.TryAdd(name, (uint)r.Index);
        animRows = rows;
        ChestIconRow = ClientTables.TableOrEmpty("MinimapIcons").FirstOrDefault(r => r.TextOrNull("Id") == "AbyssChestActive")
            is { Table: not null } icon ? icon.Index : -1;
        LeagueRow = ClientTables.TableOrEmpty("CoreLeagues").FirstOrDefault(r => r.TextOrNull("Id") == "Abyss") is { Table: not null } league
            ? league.Index : LeagueChances.Abyss;
        var chest = ClientTables.TableOrEmpty("Chests").FirstOrDefault(r => r.TextOrNull("Id") == RewardChest);
        HasRewardChest = chest.Table is not null;
        ChestQuantityPct = HasRewardChest ? StatOf(chest, "chest_item_quantity_+%") : 0;
        ChestRarityPct = HasRewardChest ? StatOf(chest, "chest_item_rarity_+%") : 0;
        Families = LoadFamilies();
    }

    static int StatOf(ClientRow chest, string statId)
    {
        var mods = ClientTables.TableOrEmpty("Mods");
        var stats = ClientTables.TableOrEmpty("Stats");
        int total = 0;
        foreach (int key in Keys(chest, "ModsKeys"))
            if (mods.Row(key) is { } mod && mod.IntOrNull("StatsKey1") is int stat
                && stats.Row(stat)?.TextOrNull("Id") == statId)
                total += mod.IntOrNull("Stat1Min") ?? 0;
        return total;
    }

    static List<(string Name, string[] Regular, string Elite)> LoadFamilies()
    {
        var families = new List<(string, string[], string)>();
        var wanted = new Dictionary<int, string>();
        foreach (var r in ClientTables.TableOrEmpty("WorldAreas"))
            if (r.TextOrNull("Id") switch { "AbyssLeagueBoss" => "Pit", "AbyssLeagueBoss2" => "Lightless", _ => null } is { } name)
                wanted[r.Index] = name;
        foreach (var r in ClientTables.TableOrEmpty("InvasionMonstersPerArea"))
        {
            if (r.IntOrNull("WorldAreasKey") is not int world || !wanted.TryGetValue(world, out string? family)) continue;
            var first = Keys(r, "MonsterVarietiesKeys1");
            var second = Keys(r, "MonsterVarietiesKeys2");
            if (first.Length == 0) continue;
            string[] regular = [.. first[..^1].Concat(second).Select(row => Monsters.Find(row.ToString())?.Id).OfType<string>()];
            if (regular.Length > 0 && Monsters.Find(first[^1].ToString())?.Id is { } elite) families.Add((family, regular, elite));
        }
        return families;
    }

    static int[] Keys(ClientRow row, string column) => row.Has(column) && !row.IsNull(column) ? row.Ints(column) : [];
}

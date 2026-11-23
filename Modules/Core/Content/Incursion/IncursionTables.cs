using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Incursion;

/// <summary>
/// The Incursion encounter's numbers and tables. Every value says where it comes from: [data] a client table,
/// [capture] measured on the official captures, [external] the wiki, [ESTIMATE] a value none of those gives. Rows are
/// looked up by their Id or key column, never by number. Built once by <see cref="LoadGameData"/>.
/// </summary>
public static class IncursionTables
{
    /// <summary>Alva spots per area [capture: 3 Alva entries in the area header of each of the 7 captured Incursion
    /// maps].</summary>
    public const int SpotsPerArea = 3;
    /// <summary>How far apart two Alva spots stand: the closest pair in the captures' areas that have several is 100 apart
    /// (one area's two portals), the next 310 and 352 [capture; the generator's own spacing is not in them].</summary>
    public const int SpotSeparation = 100;
    /// <summary>The portal is created when the character is this close to its spot, and Alva when this close to her: the
    /// official creates came at 217..325 from the character for the portal (its closest 218, 14 of 14 within 325) and at
    /// 28.6..55.5 for Alva (14 of 14), measured against the character's last known cell in 7 captured areas [capture].</summary>
    public const int PortalDistance = 300, AlvaDistance = 55;
    /// <summary>IncursionPortal1 stands at Alva + (9, −1) [capture 13/13].</summary>
    public static readonly (int X, int Y) Portal1Offset = (9, -1);
    /// <summary>When a run ends the character is put at Alva + (−3, −9) [capture 1/1].</summary>
    public static readonly (int X, int Y) ExitOffset = (-3, -9);

    /// <summary>The room's monsters: 150..187 [capture: 150..186 created per room, the 0x164 denominators
    /// 151..187].</summary>
    public const int MonstersLo = 150, MonstersHi = 187;
    /// <summary>Which share of them is the cultist family [ESTIMATE; capture 0.36..0.66].</summary>
    public const double CultistShare = 0.5;
    /// <summary>A room monster is magic with this chance and rare with this one [capture: 182 and 11 of 1777].</summary>
    public const double MagicChance = 0.10, RareChance = 0.006;
    /// <summary>How far from its pack spot a monster appears [ESTIMATE].</summary>
    public const double PackSpread = 10;
    /// <summary>The timer starts this many frames (30 a second) after the room is entered [capture 25 / 27 / 28 /
    /// 34].</summary>
    public const int StartDelayFrames = 28;
    public const double FrameSeconds = 1.0 / 30;
    /// <summary>A cursed treasure costs this many seconds [external; capture: −7 at the whole second, 4/4].</summary>
    public const double TreasureCostSeconds = 8;

    /// <summary>The cultist families and the other packs: MonsterPacks ids with the <c>incursion-memoryline-map-</c>
    /// prefix [capture: every one of the 11 captured rooms is one cultist family and one of the other three, the same
    /// pair in every room of one map].</summary>
    public static readonly string[] CultistPacks = ["fire1", "lightning1", "blood1", "chaos1"];
    public static readonly string[] OtherPacks = ["construct1", "snake1", "fragment1"];
    const string PackPrefix = "incursion-memoryline-map-";

    /// <summary>The pair of packs an area's rooms draw from, uniform [ESTIMATE].</summary>
    public static (string Cultist, string Other) Families(Random rng) =>
        (CultistPacks[rng.Next(CultistPacks.Length)], OtherPacks[rng.Next(OtherPacks.Length)]);

    /// <summary>One entry of an Incursion pack: the map variety it spawns and its weight.</summary>
    public sealed record PackEntry(string Path, int Row, int Weight);

    /// <summary>A pack's entries: MonsterPackEntries of the <c>incursion-memoryline-map-&lt;family&gt;</c> row, each
    /// entry's variety without its <c>MemoryLine</c> suffix (VaalCultistDaggersFireMapMemoryLine →
    /// VaalCultistDaggersFireMap) [data]. Only varieties the server can write are kept.</summary>
    public static IReadOnlyList<PackEntry> PackEntries(string family) => _packs.GetValueOrDefault(family, []);

    /// <summary>One IncursionArchitect row: its monster variety and the level it needs [data].</summary>
    public sealed record Architect(int Row, string Path, int MinLevel);

    /// <summary>One IncursionRooms row [data]: its tier, the room file the past room is built from, its HASH16, its
    /// architect (null for a filler) and the room it upgrades to.</summary>
    public sealed record Room(int Row, string Id, int Tier, string PastArm, int Hash16, int? ArchitectKey, int? RoomUpgrade);

    /// <summary>One IncursionBrackets row [data]: the level it starts at, the seconds a run starts with, and per rarity
    /// (normal, magic, rare, unique) what a kill adds, over a divisor. The columns are the row's fields in order
    /// (MinLevel, the two WorldAreas keys, then <c>__col3</c> the four numbers, <c>__col4</c> the divisor and
    /// <c>__col5</c> the start seconds): at MinLevel 68 they read [0.5, 1.5, 5, 0], 3 and 10, which is the 10 s
    /// every captured level-68 run starts with and 0.17 / 0.5 / 1.67 / 0 s a kill [capture: +0.218 s per kill over 1513
    /// kills in 7 maps, this rule predicts 0.212].</summary>
    public sealed record Bracket(int MinLevel, int StartSeconds, IReadOnlyList<double> Kill, double Divisor);

    static Dictionary<string, IReadOnlyList<PackEntry>> _packs = [];
    static IReadOnlyList<Architect> _architects = [];
    static IReadOnlyList<Room> _rooms = [];
    static IReadOnlyList<Room> _fillers = [];
    static IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> _families = new Dictionary<int, IReadOnlyDictionary<int, int>>();
    static IReadOnlyList<Bracket> _brackets = [];
    static int? _rulesetRow;
    static int _leagueRow = -1;

    /// <summary>Builds the tables (the module's LoadData).</summary>
    public static void LoadGameData()
    {
        var all = global::Game.Monsters.All;
        _architects = [.. ClientTables.TableOrEmpty("IncursionArchitect")
            .Where(r => r.IntOrNull("MonsterVarietiesKey") is int key && key >= 0 && key < all.Count)
            .Select(r => new Architect(r.Index, all[r.Int("MonsterVarietiesKey")].Id, r.IntOrNull("MinLevel") ?? 0))];
        _rooms = [.. ClientTables.TableOrEmpty("IncursionRooms").Select(r => new Room(r.Index, r.TextOrNull("Id") ?? "",
            r.IntOrNull("Tier") ?? 0, r.TextOrNull("PastARMFile") ?? "", r.IntOrNull("HASH16") ?? 0,
            r.IntOrNull("IncursionArchitectKey"), r.IntOrNull("RoomUpgrade_IncursionRoomsKey")))];
        // The temple's filler rooms: tier 0 with a past room, not the Entrance, the Apex or a TEMPLATE_ door piece. In
        // 3.29.3c that is 10 rooms, each 19 × 13 tiles with a telepad, two architect spawns, 18 pack spots and 10 cursed
        // treasure spots.
        _fillers = [.. _rooms.Where(r => r.Tier == 0 && r.PastArm.Length > 0 && r.Id is not ("EntranceRoom" or "Boss")
            && !r.Id.StartsWith("TEMPLATE", StringComparison.Ordinal))];
        // Architect row → tier → IncursionRooms row.
        var families = new Dictionary<int, Dictionary<int, int>>();
        foreach (var room in _rooms)
            if (room.ArchitectKey is { } architect)
                (families.TryGetValue(architect, out var tiers) ? tiers : families[architect] = []).TryAdd(room.Tier, room.Row);
        _families = families.ToDictionary(f => f.Key, f => (IReadOnlyDictionary<int, int>)f.Value);
        _brackets = [.. ClientTables.TableOrEmpty("IncursionBrackets").Select(r => new Bracket(r.IntOrNull("MinLevel") ?? 0,
            r.IntOrNull("__col5") ?? 0, r.Reals("__col3"), r.RealOrNull("__col4") ?? 1))];
        _leagueRow = ClientTables.TableOrEmpty("CoreLeagues").FirstOrDefault(r => r.TextOrNull("Id") == "Incursion") is { } league ? league.Index : -1;
        _rulesetRow = ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.TextOrNull("Id") == "IncursionRules") is { } rules
            ? rules.Index : null;
        _packs = LoadPacks();
        IncursionRoom.LoadGameData(_rooms.Select(r => r.PastArm));
        IncursionTemple.LoadGameData();
    }

    static Dictionary<string, IReadOnlyList<PackEntry>> LoadPacks()
    {
        var byRow = new Dictionary<int, string>();
        foreach (var r in ClientTables.TableOrEmpty("MonsterPacks"))
            if (r.TextOrNull("Id") is { } id && id.StartsWith(PackPrefix, StringComparison.Ordinal))
                byRow[r.Index] = id[PackPrefix.Length..];
        var all = global::Game.Monsters.All;
        var known = all.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var packs = new Dictionary<string, List<PackEntry>>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("MonsterPackEntries"))
        {
            if (r.IntOrNull("MonsterPacksKey") is not int pack || !byRow.TryGetValue(pack, out string? family)) continue;
            if (r.IntOrNull("MonsterVarietiesKey") is not int key || key < 0 || key >= all.Count) continue;
            if (MapVariety(all[key].Id, known) is not { } path || global::Game.Monsters.Find(path) is not { Supported: true } variety) continue;
            (packs.TryGetValue(family, out var list) ? list : packs[family] = []).Add(new PackEntry(variety.Id, variety.Row, r.IntOrNull("Weight") ?? 0));
        }
        return packs.ToDictionary(p => p.Key, p => (IReadOnlyList<PackEntry>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>A memory-line entry's map variety: without <c>MemoryLine</c> (and a trailing underscore), the row that
    /// exists.</summary>
    static string? MapVariety(string source, HashSet<string> known)
    {
        foreach (string suffix in (ReadOnlySpan<string>)["MemoryLine__", "MemoryLine_", "MemoryLine"])
        {
            if (!source.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string stem = source[..^suffix.Length];
            foreach (string candidate in (ReadOnlySpan<string>)[stem, stem + "_", stem.TrimEnd('_')])
                if (candidate != source && known.Contains(candidate)) return candidate;
            return null;
        }
        return null;
    }

    /// <summary>One draw from a pack's entries by MonsterPackEntries.Weight [data].</summary>
    public static PackEntry? Draw(IReadOnlyList<PackEntry> entries, Random rng)
    {
        if (entries.Count == 0) return null;
        long total = entries.Sum(e => (long)Math.Max(0, e.Weight));
        if (total <= 0) return entries[0];
        double x = rng.NextDouble() * total;
        foreach (var entry in entries)
        {
            x -= Math.Max(0, entry.Weight);
            if (x < 0) return entry;
        }
        return entries[^1];
    }

    /// <summary>The architects a level may meet: IncursionArchitect rows with MinLevel ≤ level whose variety the server
    /// can write [data; capture 22/22 architects unique, at the area level].</summary>
    public static IReadOnlyList<Architect> Architects(int level) =>
        [.. _architects.Where(a => a.MinLevel <= level && global::Game.Monsters.Find(a.Path) is { Supported: true })];

    /// <summary>An IncursionArchitect row, whether or not the server can write its variety.</summary>
    public static Architect? ArchitectByRow(int row) => _architects.FirstOrDefault(a => a.Row == row);

    /// <summary>An architect's MonsterVarieties HASH16, what the 0x223 temple state carries for a run's architects
    /// [capture 4/4]; 0 for an unknown row.</summary>
    public static ushort ArchitectHash16(int row) =>
        ArchitectByRow(row) is { Path: { } path } && global::Game.Monsters.Find(path) is { } variety ? variety.Hash16 : (ushort)0;

    /// <summary>Every IncursionRooms row, by row.</summary>
    public static IReadOnlyList<Room> AllRooms => _rooms;

    /// <summary>The temple's filler rooms (see <see cref="LoadGameData"/>).</summary>
    public static IReadOnlyList<Room> FillerRooms => _fillers;

    /// <summary>The temple's families: architect row → tier → IncursionRooms row [data].</summary>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> TempleFamilies => _families;

    /// <summary>A room's IncursionRooms HASH16, 0 for a row out of range.</summary>
    public static ushort RoomHash16(int room) => room >= 0 && room < _rooms.Count ? (ushort)_rooms[room].Hash16 : (ushort)0;

    /// <summary>The closed-door spots of a past room, by direction: the offset from the room centre and the rotation
    /// [capture 107/107].</summary>
    public static readonly IReadOnlyDictionary<string, ((int X, int Y) Offset, float Rotation)> DoorSpots =
        new Dictionary<string, ((int, int), float)>(StringComparer.Ordinal)
        {
            ["tl"] = ((-138, -205), MathF.PI), ["tr"] = ((138, -205), MathF.PI),
            ["bl"] = ((-138, 205), 0f), ["br"] = ((138, 205), 0f),
            ["l"] = ((-274, 0), MathF.PI / 2), ["r"] = ((274, 0), 3 * MathF.PI / 2),
        };

    /// <summary>The level's bracket: the last row whose MinLevel ≤ level, else the first.</summary>
    public static Bracket? BracketFor(int level)
    {
        Bracket? found = null;
        foreach (var bracket in _brackets)
            if (bracket.MinLevel <= level) found = bracket;
        return found ?? _brackets.FirstOrDefault();
    }

    /// <summary>The seconds a kill of this rarity adds (see <see cref="Bracket"/>).</summary>
    public static double KillSeconds(int level, MonsterRarity rarity)
    {
        if (BracketFor(level) is not { } bracket) return 0;
        int at = (int)rarity;
        if (at < 0 || at >= bracket.Kill.Count) return 0;
        return bracket.Kill[at] / (bracket.Divisor == 0 ? 1 : bracket.Divisor);
    }

    /// <summary>The seconds a run starts with [data; capture: 10 at levels 68 and 69, 11 times].</summary>
    public static int StartSeconds(int level) => BracketFor(level)?.StartSeconds ?? 0;

    /// <summary>The CoreLeagues row of the Incursion, looked up by its Id (a bit of MapDisableLeagues); -1 without the
    /// table.</summary>
    public static int LeagueRow => _leagueRow;

    /// <summary>The Rulesets row <c>IncursionRules</c>, null when the table has none.</summary>
    public static byte? RulesetRow => _rulesetRow is int row && row is >= 0 and <= byte.MaxValue ? (byte)row : null;
}

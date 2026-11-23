using GameServer.Core;

/// <summary>
/// How many of a league mechanic an area gets, the way the client rolls it (<c>0x141ea8800</c> → <c>0x141ea8cb0</c>):
/// the area's WorldAreaLeagueChances row (WorldAreas <c>LeagueChance1</c>, <c>LeagueChance2</c> with world +0xd7)
/// gives each CoreLeagues row a chance and, for some, a repeat chance. One roll at the chance, then rolls at the repeat
/// chance while they succeed, up to the row's most per area (+0x8a). A row marked "not rolled" (+0xa0) gives none,
/// one marked "needs a chance" (+0x8e) none without one. The level generator rolls the rows that change the terrain
/// itself (<c>ExtraTerrainFeatures</c>); the others (Rogue Exiles, Tormented Spirits, Essences, strongboxes) are the
/// server's, and a league's modules roll them here. Agents/docs/game/league-encounters.md.
/// </summary>
public sealed class LeagueChances
{
    public LeagueChances()
    {
        _data = Load();
    }

    /// <summary>CoreLeagues rows, by index.</summary>
    public const int Anarchy = 0, Shrine = 1, VaalSideArea = 2, Ambush = 3, Beyond = 4, Torment = 5, Essence = 6, Breach = 7,
        Harbinger = 8, Abyss = 9, Legion = 15, Blight = 16, Delirium = 17, Harvest = 18, Heist = 19, Ritual = 20, Expedition = 21,
        Ultimatum = 24, Settlers = 27, Mercenaries = 28;

    /// <summary><c>0x141ea8800</c>'s jump table: per CoreLeagues row, the WorldAreaLeagueChances byte offsets of its chance
    /// and its repeat chance (0: none). Rows 4 and 8 (Beyond, Harbinger) read stats only; 10 and 29 nothing.</summary>
    static readonly (int Chance, int Repeat)[] Offsets =
    [
        (0x8, 0), (0x24, 0x28), (0x60, 0), (0x64, 0x68), (0, 0), (0xc, 0x10), (0x38, 0x3c), (0x40, 0), (0, 0), (0x44, 0),
        (0, 0), (0x88, 0), (0x8c, 0), (0x90, 0), (0x94, 0), (0x2c, 0), (0x48, 0), (0x30, 0), (0x34, 0), (0x4c, 0),
        (0x50, 0), (0x54, 0), (0x5c, 0), (0x70, 0), (0x74, 0), (0x78, 0), (0x98, 0), (0x9c, 0xa0), (0xa4, 0), (0, 0),
    ];

    public sealed record League(string Id, int MostPerArea, bool NeedsChance, bool NotRolled);

    sealed record Data(Dictionary<string, (int Normal, int Alternate)> Areas, List<int[]> Chances, List<League> Leagues);

    readonly Data? _data;

    public static LeagueChances Current { get; } = new();

    /// <summary>The CoreLeagues row, or null without the tables.</summary>
    public League? LeagueOf(int row) => _data is { } d && (uint)row < (uint)d.Leagues.Count ? d.Leagues[row] : null;

    /// <summary>The area's chance for the CoreLeagues row and its repeat chance, in percent, before area stats.</summary>
    public (int Chance, int Repeat) ChanceOf(string areaId, int league, bool alternate = false)
    {
        if (_data is not { } d || (uint)league >= (uint)Offsets.Length || !d.Areas.TryGetValue(areaId, out var rows)) return (0, 0);
        int row = alternate ? rows.Alternate : rows.Normal;
        if ((uint)row >= (uint)d.Chances.Count) return (0, 0);
        var (at, repeatAt) = Offsets[league];
        int Read(int offset) => offset == 0 ? 0 : d.Chances[row].ElementAtOrDefault((offset - 8) / 4);
        return (Read(at), Read(repeatAt));
    }

    /// <summary>How many the area gets. <paramref name="increased"/> and <paramref name="added"/> are the two area stats
    /// the client mixes in (<c>0x3fd5</c>, <c>0x3fe5</c>: chance × (100 + increased) / 100 + added); both are 0 without
    /// map mods.</summary>
    public int Count(string areaId, int league, Random rng, bool alternate = false, int increased = 0, int added = 0)
    {
        if (LeagueOf(league) is not { } row || row.NotRolled || row.MostPerArea <= 0) return 0;
        var (chance, repeat) = ChanceOf(areaId, league, alternate);
        // Game_LEAGUE_CHANCES=Anarchy=100,Essence=50: a test's chance for every area that is not a town or hideout.
        if (Forced(row.Id) is int forced) chance = forced;
        if (row.NeedsChance && chance <= 0) return 0;
        chance = Math.Clamp(chance * (100 + increased) / 100 + added, 0, 100);
        repeat = Math.Clamp(repeat, 0, 100);
        // 0x14205b2a0: at least 1, and 100 or a draw below it.
        bool Roll(int c) => c >= 1 && (c >= 100 || rng.Next(100) < c);
        int count = 0;
        if (Roll(chance))
        {
            count++;
            if (repeat != 0)
                while (count < row.MostPerArea && Roll(repeat)) count++;
        }
        return count;
    }

    /// <summary>The chance Game_LEAGUE_CHANCES gives the CoreLeagues row by its Id (a test's override), or null.</summary>
    public static int? Forced(string league)
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES") ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals(league, StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    static Data? Load()
    {
        if (!ClientTables.Has("WorldAreaLeagueChances") || !ClientTables.Has("CoreLeagues") || !ClientTables.Has("WorldAreas")) return null;
        var areas = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var a in ClientTables.Table("WorldAreas"))
            if (a.Text("Id") is { } id)
                areas.TryAdd(id, (a.IntOrNull("LeagueChance1") ?? -1, a.IntOrNull("LeagueChance2") ?? -1));
        // Every column after Id is an i32 at 8 + 4 × (n − 1).
        var table = ClientTables.Table("WorldAreaLeagueChances");
        string[] columns = [.. table.Columns.Where(c => c is not ("_row" or "Id"))];
        var chances = table.Select(r => columns.Select(c => r.IntOrNull(c) ?? 0).ToArray()).ToList();
        // CoreLeagues: +0x8a most per area (__col11), +0x8e needs a chance (__col12), +0xa0 not rolled (__col15).
        var leagues = ClientTables.Table("CoreLeagues").Select(r => new League(r.Text("Id") ?? "", r.IntOrNull("__col11") ?? 0,
            r.BoolOrNull("__col12") ?? false, r.BoolOrNull("__col15") ?? false)).ToList();
        return new Data(areas, chances, leagues);
    }
}

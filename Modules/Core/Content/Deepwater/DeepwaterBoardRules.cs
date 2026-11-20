using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Deepwater;

/// <summary>A board's rules as the client's validator <c>0x141fbeda0</c> and the planner <c>0x14041b830</c> apply them, and
/// the stats of each cell (<c>0x141fbe9d0</c>) and of the whole Voyage (<c>0x141fbe820</c>). A board is n × n cells, cell
/// y·n + x, y up, cell 0 the bottom-left start. A cell's open sides are its room's mask (DeepwaterRooms <c>__col2</c>, bits
/// 1/2/4/8 Left/Up/Right/Down) turned by the chart's rotation byte.</summary>
internal static class DeepwaterBoardRules
{
    /// <summary>A placed chart: its DeepwaterChart component and its mods.</summary>
    public readonly record struct Cell(ChartData Chart, IReadOnlyList<ItemMod> Mods);

    /// <summary>ClientStrings rows of the validator's errors (its code is the row + 1).</summary>
    public const int Incomplete = 8396, Connections = 8397, BossLevel = 8468, TwoBosses = 8469, WrongSize = 1273;
    /// <summary>The area level a boss room (Kishara's Rest) needs.</summary>
    public const int BossMinimumLevel = 83;
    /// <summary>The 12 border segments: a 3 × 3 board's cell i gets these (the window's table at <c>0x140414970</c>).</summary>
    static readonly int[][] Segments = [[0, 11], [1], [2, 3], [10], [], [4], [8, 9], [7], [5, 6]];

    sealed record RoomInfo(int Mask, bool Boss, bool BossEntry, (uint Key, int Value)[] Stats);

    static readonly Lazy<RoomInfo[]> Rooms = new(() => [.. ClientTables.TableOrEmpty("DeepwaterRooms").Select(r =>
    {
        int[] stats = r.IsNull("Stats") ? [] : r.Ints("Stats");
        int[] values = r.IsNull("StatValues") ? [] : r.Ints("StatValues");
        return new RoomInfo(r.IntOrNull("__col2") ?? 0, r.BoolOrNull("__col17") == true, !r.IsNull("__col14"),
            [.. stats.Select((s, i) => ((uint)s + 1, i < values.Length ? values[i] : 0))]);
    })]);

    static RoomInfo? Room(byte row) => row < Rooms.Value.Length ? Rooms.Value[row] : null;

    /// <summary>The client's turn of a mask by a rotation (as <c>DeepwaterGrid.Open</c>).</summary>
    public static int Open(int mask, int rotation) => ((mask << (4 - (rotation & 3))) | (mask >> (rotation & 3))) & 15;

    /// <summary>The cell's open sides.</summary>
    public static int Open(ChartData chart) => Room(chart.Room) is { } room ? Open(room.Mask, chart.Rotation) : 0;

    /// <summary>Why the client refuses this board (a ClientStrings row), or null: n of 1, 2 or 3; every cell filled; each
    /// shared edge open on both sides or closed on both (the rim is free, and there is no reachability test); a boss room
    /// only at <see cref="BossMinimumLevel"/> and at most once.</summary>
    public static int? Validate(IReadOnlyList<Cell?> cells, int n)
    {
        if (n is < 1 or > 3 || cells.Count != n * n) return WrongSize;
        if (cells.Any(c => c is null)) return Incomplete;
        for (int i = 0; i < cells.Count; i++)
        {
            int x = i % n, y = i / n, open = Open(cells[i]!.Value.Chart);
            if (x + 1 < n && ((open & 4) != 0) != ((Open(cells[i + 1]!.Value.Chart) & 1) != 0)) return Connections;
            if (y + 1 < n && ((open & 2) != 0) != ((Open(cells[i + n]!.Value.Chart) & 8) != 0)) return Connections;
        }
        int kishara = cells.Count(c => Room(c!.Value.Chart.Room) is { } r && (r.Boss || r.BossEntry));
        if (kishara > 1) return TwoBosses;
        int bosses = cells.Count(c => Room(c!.Value.Chart.Room)?.Boss == true);
        if (bosses == 1 && Level(cells) < BossMinimumLevel) return BossLevel;
        return null;
    }

    /// <summary>The run's area level: the charts' average (floored) + 10, at most the highest chart's, within 1..100.</summary>
    public static int Level(IReadOnlyList<Cell?> cells)
    {
        var levels = cells.OfType<Cell>().Select(c => (int)c.Chart.AreaLevel).ToList();
        if (levels.Count == 0) return 1;
        return Math.Clamp(Math.Min(levels.Sum() / levels.Count + 10, levels.Max()), 1, 100);
    }

    /// <summary>The border modifiers of a cell of a 3 × 3 board (none on a smaller board or with fewer than 12 mods).</summary>
    public static IEnumerable<ItemMod> BorderOf(int i, int n, IReadOnlyList<ItemMod> border) =>
        n == 3 && border.Count == 12 && i is >= 0 and < 9 ? Segments[i].Select(s => border[s]) : [];

    /// <summary>A cell's area stats by client stat key (Stats row + 1), as the planner shows them: the chart's own explicit
    /// affixes scaled by its border mods' <c>deepwater_border_mod_affected_chart_modifier_effect_+%</c> (truncated per mod),
    /// the neighbours' implicits that apply to adjacent charts, the cell's border mods and its room's Stats; then the
    /// per-connection stats turned into their map stats times the cell's sides that meet a neighbour.</summary>
    public static Dictionary<uint, int> CellStats(ModCatalog mods, IReadOnlyList<Cell?> cells, int n, int i, IReadOnlyList<ItemMod> border)
    {
        var stats = new Dictionary<uint, int>();
        if (i < 0 || i >= cells.Count || cells[i] is not { } cell) return stats;
        var own = BorderOf(i, n, border).ToList();
        int effect = own.Sum(m => Value(mods, m, ChartEffect));
        foreach (var m in cell.Mods)
            if (m.Section == ModSection.Explicit) Add(mods, stats, m, effect);
        int x = i % n, y = i / n;
        foreach (var (dx, dy) in Sides)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= n || ny >= n || cells[ny * n + nx] is not { } near) continue;
            foreach (var m in near.Mods)
                if (m.Section == ModSection.Implicit && Has(mods, m, Adjacent)) Add(mods, stats, m, 0);
        }
        foreach (var m in own) Add(mods, stats, m, 0);
        if (Room(cell.Chart.Room) is { } room)
            foreach (var (key, value) in room.Stats) stats[key] = stats.GetValueOrDefault(key) + value;
        int connections = Connected(cells, n, i);
        foreach (var (from, to) in PerConnection)
            if (Program.Stats.Key(from) is uint f && stats.Remove(f, out int per) && Program.Stats.Key(to) is uint t)
                stats[t] = stats.GetValueOrDefault(t) + per * connections;
        return stats;
    }

    /// <summary>The Voyage-wide area stats: every chart's implicits that apply globally.</summary>
    public static Dictionary<uint, int> GlobalStats(ModCatalog mods, IReadOnlyList<Cell?> cells)
    {
        var stats = new Dictionary<uint, int>();
        foreach (var cell in cells.OfType<Cell>())
            foreach (var m in cell.Mods)
                if (m.Section == ModSection.Implicit && Has(mods, m, Global)) Add(mods, stats, m, 0);
        return stats;
    }

    /// <summary>The chance (0..100) that a Voyage keeps the chart of cell <paramref name="i"/>: its border mods'
    /// <c>deepwater_border_mod_chance_%_to_not_consume_affected_chart</c> summed (G: the corner's two segments add).</summary>
    public static int KeepChance(ModCatalog mods, int i, int n, IReadOnlyList<ItemMod> border) =>
        Math.Clamp(BorderOf(i, n, border).Sum(m => Value(mods, m, KeepChart)), 0, 100);

    const string KeepChart = "deepwater_border_mod_chance_%_to_not_consume_affected_chart";
    const string ChartEffect = "deepwater_border_mod_affected_chart_modifier_effect_+%";
    const string Adjacent = "local_deepwater_mod_applies_to_adjacent_charts", Global = "local_deepwater_mod_applies_globally";
    static readonly (int Dx, int Dy)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    static readonly (string From, string To)[] PerConnection =
    [
        ("chart_number_of_rare_packs_+%_per_connection", "map_number_of_rare_packs_+%"),
        ("chart_item_drop_quantity_+%_per_connection", "map_item_drop_quantity_+%"),
    ];
    // Scope stats: they say where a mod applies, not what it does.
    static readonly HashSet<string> Scope = [Adjacent, Global];

    /// <summary>How many of the cell's open sides meet a neighbour's open side.</summary>
    static int Connected(IReadOnlyList<Cell?> cells, int n, int i)
    {
        if (cells[i] is not { } cell) return 0;
        int open = Open(cell.Chart), x = i % n, y = i / n, count = 0;
        foreach (var (bit, back, dx, dy) in new[] { (4, 1, 1, 0), (1, 4, -1, 0), (2, 8, 0, 1), (8, 2, 0, -1) })
        {
            int nx = x + dx, ny = y + dy;
            if ((open & bit) != 0 && nx >= 0 && ny >= 0 && nx < n && ny < n && cells[ny * n + nx] is { } near && (Open(near.Chart) & back) != 0) count++;
        }
        return count;
    }

    static bool Has(ModCatalog mods, ItemMod m, string stat) => mods.ByHash(m.Hash)?.Stats.Any(s => s.Id == stat) == true;

    static int Value(ModCatalog mods, ItemMod m, string stat)
    {
        if (mods.ByHash(m.Hash) is not { } mod) return 0;
        int total = 0;
        for (int s = 0; s < mod.Stats.Count && s < m.Values.Count; s++)
            if (mod.Stats[s].Id == stat) total += m.Values[s];
        return total;
    }

    static void Add(ModCatalog mods, Dictionary<uint, int> stats, ItemMod m, int effect)
    {
        if (mods.ByHash(m.Hash) is not { } mod) return;
        for (int s = 0; s < mod.Stats.Count && s < m.Values.Count; s++)
        {
            if (Scope.Contains(mod.Stats[s].Id) || Program.Stats.Key(mod.Stats[s].Id) is not uint key) continue;
            int value = effect == 0 ? m.Values[s] : (int)((long)m.Values[s] * (100 + effect) / 100);
            stats[key] = stats.GetValueOrDefault(key) + value;
        }
    }
}

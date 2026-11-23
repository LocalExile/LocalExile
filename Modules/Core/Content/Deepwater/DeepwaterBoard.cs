using GameServer.Core;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Modules.Deepwater;

/// <summary>An encounter instance's geometry, from its generated level (<see cref="ServerLevels.Level.Records"/>): each
/// cell of the board with its rectangle, DeepwaterRooms row, biome area and placed room's named points, and the deck of
/// The Sovereign the plugin adds (record 1). Cells are matched to records by rectangle (the records of one cell's room,
/// row-major from the board's bottom-left as DeepwaterGrid documents them), not by index: the rectangles order by y then
/// x as the plugin's nodes do (Agents/docs/re/011-level-generation.md §3c4).</summary>
internal sealed class DeepwaterBoard
{
    /// <summary>A cell: its index N·y + x, its rectangle in grid cells (end exclusive), its DeepwaterRooms row, its
    /// biome's world area (WorldAreas Id, DeepwaterBiomes.WorldArea) and its room's named points placed in the level.</summary>
    public sealed record Cell(int Index, int X0, int Y0, int X1, int Y1, int Room, string? BiomeArea, PlacedRoom Placed,
        IReadOnlyList<(string Name, int X, int Y, float Angle)> Points)
    {
        public bool Contains((int X, int Y) at) => at.X >= X0 && at.X < X1 && at.Y >= Y0 && at.Y < Y1;
        public (int X, int Y)? Point(string name) => Points.FirstOrDefault(p => p.Name == name) is { Name: not null } p ? (p.X, p.Y) : null;
    }

    public required int Size { get; init; }
    public required IReadOnlyList<Cell> Cells { get; init; }
    /// <summary>The deck's rectangle (record 1), or null when the level has none.</summary>
    public (int X0, int Y0, int X1, int Y1)? Deck { get; init; }
    /// <summary>The deck's <c>default</c> point, where an extraction lands.</summary>
    public (int X, int Y)? DeckDefault { get; init; }

    public Cell? CellAt((int X, int Y) at) => Cells.FirstOrDefault(c => c.Contains(at));

    public bool OnDeck((int X, int Y) at) =>
        Deck is { } d && at.X >= d.X0 && at.X < d.X1 && at.Y >= d.Y0 && at.Y < d.Y1;

    // DeepwaterRooms: the .arm path → row, and the row's biome's world area Id.
    static (Dictionary<string, int> ByArm, Dictionary<int, string?> Biome) Rooms = ([], []);

    public static void LoadGameData()
    {
        var areas = ClientTables.TableOrEmpty("WorldAreas");
        var biomes = ClientTables.TableOrEmpty("DeepwaterBiomes");
        string? AreaOf(long? biome) => biome is { } b && b >= 0 && b < biomes.Count && biomes[(int)b].LongOrNull("WorldArea") is { } a
            && a >= 0 && a < areas.Count ? areas[(int)a].Text("Id") : null;
        var byArm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var biome = new Dictionary<int, string?>();
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterRooms"))
        {
            if (r.Text("ArmFiles") is { } arm) byArm.TryAdd(arm, r.Index);
            biome[r.Index] = AreaOf(r.LongOrNull("Biome"));
        }
        Rooms = (byArm, biome);
    }

    /// <summary>The board of a generated encounter level, or null when the level has no Deepwater cells (a capture, or
    /// generation off). <paramref name="log"/> gets the cell-to-record mapping.</summary>
    public static DeepwaterBoard? Of(ServerLevels.Level? level, Action<string>? log = null)
    {
        if (level is null || level.Records.Count < 3) return null;
        var rooms = Rooms;
        var cells = new List<(DoodadRecord Record, PlacedRoom Room, int Row)>();
        foreach (var record in level.Records.Skip(2))
            if (record.Rooms.Count == 1 && rooms.ByArm.TryGetValue(record.Rooms[0].Room.Path, out int row))
                cells.Add((record, record.Rooms[0], row));
        int n = (int)Math.Round(Math.Sqrt(cells.Count));
        if (n == 0 || n * n != cells.Count) { log?.Invoke($"board: {cells.Count} cell records, not a square"); return null; }
        var ordered = cells.OrderBy(c => c.Record.Y).ThenBy(c => c.Record.X).ToList();
        var board = new List<Cell>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var (record, room, row) = ordered[i];
            var points = room.Room.Points.Select(p =>
            {
                var (x, y, angle) = AreaObjects.Place(room, p.X, p.Y, p.Angle);
                return (p.Name, x, y, angle);
            }).ToList();
            board.Add(new Cell(i, record.X * 23, record.Y * 23, (record.X + record.W) * 23, (record.Y + record.H) * 23, row,
                rooms.Biome.GetValueOrDefault(row), room, points));
        }
        var deck = level.Records[1];
        (int, int)? deckDefault = null;
        foreach (var room in deck.Rooms)
            if (room.Room.Points.FirstOrDefault(p => p.Name == "default") is { } p)
            {
                var (x, y, _) = AreaObjects.Place(room, p.X, p.Y, p.Angle);
                deckDefault = (x, y);
                break;
            }
        log?.Invoke($"board {n}×{n}: " + string.Join("; ", board.Select(c =>
            $"cell {c.Index} = ({c.X0}, {c.Y0})-({c.X1}, {c.Y1}) room {c.Room} {c.BiomeArea} {Path.GetFileName(c.Placed.Room.Path)}"))
            + $"; deck ({deck.X * 23}, {deck.Y * 23})-({(deck.X + deck.W) * 23}, {(deck.Y + deck.H) * 23}) default {deckDefault?.ToString() ?? "none"}");
        return new DeepwaterBoard
        {
            Size = n, Cells = board,
            Deck = (deck.X * 23, deck.Y * 23, (deck.X + deck.W) * 23, (deck.Y + deck.H) * 23),
            DeckDefault = deckDefault,
        };
    }
}

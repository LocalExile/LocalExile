using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;

namespace GameServer.Modules.Incursion;

/// <summary>
/// A past room's own geometry, read from its room file (<c>IncursionRooms.PastARMFile</c>): the size in tiles from the
/// room's key line, and every point as an offset from the room centre (<c>w * 23 / 2</c>, <c>h * 23 / 2</c>) [capture:
/// each of the 11 captured rooms is explained by exactly one room file]. A named point with no name is a monster pack
/// spot [ESTIMATE meaning], one whose name ends in <c>AlvaCursedTreasure</c> a cursed treasure, <c>telepad</c> the cell
/// the character lands on; the architects stand on the ArchitectSpawn1 / 2 doodads.
/// </summary>
/// <param name="Telepad">Where the character lands [capture 1/1].</param>
/// <param name="Spawn1">ArchitectSpawn1: the resident architect (or the first of two) [capture 11/11].</param>
/// <param name="Spawn2">ArchitectSpawn2: the other architect.</param>
/// <param name="PackSpots">The unnamed points.</param>
/// <param name="Chests">The AlvaCursedTreasure points [capture: every treasure on one, 11/11].</param>
public sealed record IncursionRoom(string Path, int Width, int Height, (int X, int Y)? Telepad, (int X, int Y)? Spawn1,
    (int X, int Y)? Spawn2, IReadOnlyList<(int X, int Y)> PackSpots, IReadOnlyList<(int X, int Y)> Chests)
{
    /// <summary>A tile is 23 × 23 cells, so a 19 × 13 room is 437 × 299 cells.</summary>
    public const int TileCells = 23;

    const string TelepadPoint = "telepad", TreasurePointSuffix = "AlvaCursedTreasure";
    const string Spawn1Object = "ArchitectSpawn1", Spawn2Object = "ArchitectSpawn2";

    /// <summary>An offset from the room centre, at a room centre on the board.</summary>
    public static (int X, int Y) At((int X, int Y) centre, (int X, int Y) offset) => (centre.X + offset.X, centre.Y + offset.Y);

    static Dictionary<string, IncursionRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the rooms' files from the game pack (the module's LoadData); a file the pack has not got is left
    /// out.</summary>
    public static void LoadGameData(IEnumerable<string> paths)
    {
        var rooms = new Dictionary<string, IncursionRoom>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            if (path.Length > 0 && !rooms.ContainsKey(path) && Parse(path) is { } room) rooms[path] = room;
        _rooms = rooms;
    }

    /// <summary>A room file, or null when the pack has not got it or it has no room key line.</summary>
    public static IncursionRoom? Read(string armPath) => _rooms.GetValueOrDefault(armPath);

    static IncursionRoom? Parse(string armPath)
    {
        byte[]? bytes;
        try { bytes = GamePack.Default.TryRead(armPath.ToLowerInvariant()); }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
        if (bytes is null) return null;
        var file = RoomFile.Parse(TerrainText.Decode(bytes));
        if (file.RoomKey is null || file.Width <= 0 || file.Height <= 0) return null;
        var centre = (X: file.Width * TileCells / 2, Y: file.Height * TileCells / 2);
        (int X, int Y) Relative(float x, float y) => ((int)x - centre.X, (int)y - centre.Y);
        var packSpots = new List<(int X, int Y)>();
        var chests = new List<(int X, int Y)>();
        (int X, int Y)? telepad = null;
        foreach (var point in file.Points)
        {
            if (point.Name.Length == 0) packSpots.Add(Relative(point.X, point.Y));
            else if (point.Name.EndsWith(TreasurePointSuffix, StringComparison.OrdinalIgnoreCase)) chests.Add(Relative(point.X, point.Y));
            else if (point.Name.Equals(TelepadPoint, StringComparison.OrdinalIgnoreCase)) telepad = Relative(point.X, point.Y);
        }
        (int X, int Y)? Doodad(string leaf)
        {
            foreach (var doodad in file.Doodads)
                if (Leaf(doodad.Type).Equals(leaf, StringComparison.OrdinalIgnoreCase)) return Relative(doodad.X, doodad.Y);
            return null;
        }
        return new IncursionRoom(armPath, file.Width, file.Height, telepad, Doodad(Spawn1Object), Doodad(Spawn2Object), packSpots, chests);
    }

    static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public override string ToString() =>
        $"{Leaf(Path)} {Width}x{Height} tiles, telepad {Telepad?.ToString() ?? "none"}, {PackSpots.Count} pack spot(s), {Chests.Count} treasure spot(s)";
}

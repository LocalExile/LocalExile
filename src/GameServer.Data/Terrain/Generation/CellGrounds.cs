namespace GameServer.Data.Terrain.Generation;

/// <summary>The ground type of a generated map's cells, by the name the client's tables use: the
/// <see cref="TileGrid"/>'s own tile corners (its (Width + 1) × (Height + 1) vertices, each carrying the ground the
/// tiles wrote there) resolved to the <c>.gt</c> file's first line (<see cref="GroundType.Name"/>) — the same strings
/// MonsterPacks' <c>Grounds</c> column lists ("BeachOceanShallowWet", "ForestCaveFloor"). The walkability grid cannot
/// answer this: it is one boolean a cell, with no ground name in it.
/// <para>A cell takes the ground of its nearest corner (a corner sits every 23 cells, at the tile origins). The
/// official server's ground filter is not in the client (it reads <c>Grounds</c> into the runtime pack and never
/// places from it), so the cell rule is a reading: measured offline against captured official instances (7 instances
/// of 5 areas), nearest corner put 96 of 98 official monsters of a <c>Grounds</c> pack on a listed ground, the four
/// corners of the cell's tile 97 of 98 — one monster apart. Nearest corner is the one taken.</para></summary>
public sealed class CellGrounds
{
    /// <summary>Cells a tile, so cells per corner step.</summary>
    public const int CellsPerTile = 23;

    readonly ushort[] _corners;
    readonly string[] _names;

    CellGrounds(int cornerWidth, int cornerHeight, ushort[] corners, string[] names)
    {
        (CornerWidth, CornerHeight, _corners, _names) = (cornerWidth, cornerHeight, corners, names);
        Present = new HashSet<string>(names.Skip(1), StringComparer.OrdinalIgnoreCase);
    }

    public int CornerWidth { get; }
    public int CornerHeight { get; }

    /// <summary>Every ground name this map has on a corner. Compared without case: the column itself carries both
    /// "Knoll" (MonsterPacks rows 548 and 549) and "knoll" (rows 1195 and 1202) for the one ground type of that name,
    /// so an exact match would answer differently for packs that mean the same ground.</summary>
    public IReadOnlySet<string> Present { get; }

    /// <summary>The ground name of the cell's nearest corner; null where that corner has no ground.</summary>
    public string? NameAt(int cellX, int cellY)
    {
        int cx = Math.Clamp((cellX + CellsPerTile / 2) / CellsPerTile, 0, CornerWidth - 1);
        int cy = Math.Clamp((cellY + CellsPerTile / 2) / CellsPerTile, 0, CornerHeight - 1);
        int at = _corners[cy * CornerWidth + cx];
        return at == 0 ? null : _names[at];
    }

    /// <summary>Whether this cell's ground is one of <paramref name="grounds"/>.</summary>
    public bool On(int cellX, int cellY, IReadOnlyList<string> grounds) =>
        NameAt(cellX, cellY) is { } name && grounds.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any of <paramref name="grounds"/> is on this map at all.</summary>
    public bool HasAny(IReadOnlyList<string> grounds) => grounds.Any(Present.Contains);

    /// <summary>The layer of a generated grid. <paramref name="ground"/> is the terrain cache's reader
    /// (<c>TerrainCache.Ground</c>): the corners hold the <c>.gt</c> path, and that file's first line is the
    /// name. Built once per generated area, never on a per-frame path.</summary>
    public static CellGrounds Build(TileGrid grid, Func<string?, GroundType?> ground)
    {
        int cw = grid.Width + 1, ch = grid.Height + 1;
        var corners = new ushort[cw * ch];
        var names = new List<string> { "" };
        var index = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                string? name = ground(grid.Corner(x, y).Ground)?.Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!index.TryGetValue(name, out ushort at))
                {
                    if (names.Count > ushort.MaxValue) continue;
                    index[name] = at = (ushort)names.Count;
                    names.Add(name);
                }
                corners[y * cw + x] = at;
            }
        return new CellGrounds(cw, ch, corners, [.. names]);
    }
}

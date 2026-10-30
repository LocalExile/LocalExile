namespace GameServer.Data.Terrain.Generation;

/// <summary>The ground height of every cell of a generated map (23 cells per tile), in the client's height units
/// (× 7.8125 for world units; <c>0x141d84be0</c> interpolates between the four cells around a point).
/// <c>0x141d84a10</c>: the highest of the map's height rectangles (+0x130) that holds the cell, else
/// <c>0x141d84b00</c>: the entry's sub-tile height (<see cref="SubTileWalk.HeightAt"/>) at the cell turned back into the
/// tile's frame (<c>0x141d7d100</c>) plus map +0x104 × the entry's height (+0x30). Ported without height rectangles
/// and with +0x104 = 1 (the port's heights already carry StoreyHeight); the recorder logs both per load. An empty
/// entry, or one without walkability data, is 0.</summary>
public static class CellHeights
{
    // 0x142fb7f98: the code that turns an entry of each orientation back (1 and 3 swap); 0x142fa04b0: per code, which
    // of (22 − x, x, 22 − y, y) give the tile-frame column and row.
    static readonly byte[] Back = [0, 3, 2, 1, 4, 5, 6, 7, 8];
    static readonly byte[] Codes = [0, 1, 1, 1, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1];

    public static short[] Build(TileGrid grid, Func<TileDef, TileWalk?> walkData)
    {
        int w = grid.Width * 23, h = grid.Height * 23;
        var heights = new short[w * h];
        Span<int> local = stackalloc int[4];
        for (int ty = 0; ty < grid.Height; ty++)
            for (int tx = 0; tx < grid.Width; tx++)
            {
                if (grid[tx, ty] is not { } e || Sub(e, walkData) is not { } sub) continue;
                int c = 3 * Back[Math.Min(e.Orientation, (byte)8)];
                for (int y = 0; y < 23; y++)
                    for (int x = 0; x < 23; x++)
                    {
                        local[0] = 22 - x; local[1] = x; local[2] = 22 - y; local[3] = y;
                        int u = local[Codes[c + 1] + Codes[c] * 2], v = local[(Codes[c] == 0 ? 2 : 0) + Codes[c + 2]];
                        heights[(ty * 23 + y) * w + tx * 23 + x] = (short)(sub.HeightAt(v * 23 + u) + e.HeightOffset);
                    }
            }
        return heights;
    }

    // The entry's sub-tile, as for the walkability grids: the one the orientation puts at the entry's offset.
    static SubTileWalk? Sub(TileEntry e, Func<TileDef, TileWalk?> walkData)
    {
        if (walkData(e.Tile) is not { } data) return null;
        for (int v = 0; v < e.Tile.Height; v++)
            for (int u = 0; u < e.Tile.Width; u++)
                if (Orientation.Map(e.Orientation, u, v, e.Tile.Width, e.Tile.Height) == (e.OffsetX, e.OffsetY))
                    return v * e.Tile.Width + u < data.SubTiles.Length ? data.SubTiles[v * e.Tile.Width + u] : null;
        return null;
    }
}

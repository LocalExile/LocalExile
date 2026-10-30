namespace GameServer.Data.Terrain.Generation;

/// <summary>Room tilising (<c>0x141fea780</c>) on a view of the map: the tile graph's special nodes and overlays, and a
/// dungeon's overlays.</summary>
sealed class RoomTiler(LevelRandom rng, TileGrid grid, int offsetX, int offsetY, TileStore store)
{
    /// <summary>The rooms tilised, in order (the layout context's +0x58 list), in map entries.</summary>
    public readonly List<PlacedRoom> Rooms = [];
    /// <summary>The map vertices that took a room's own inner vertex ground (<c>0x141ffbd70</c>); the seams leave tiles
    /// touching them alone.</summary>
    public readonly HashSet<(int X, int Y)> RoomVertices = [];

    /// <summary>The listeners <c>0x141fea780</c> calls after each slot tile (tile, orientation, view x, y).</summary>
    public Action<TileDef, int, int, int>? TilePlaced;

    // 0x141fea780's per-orientation offset of a slot's tile by (1 − w, 1 − h): x += a·(1 − w) + c·(1 − h),
    // y += b·(1 − w) + d·(1 − h).
    static readonly int[] SlotShift = [0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0];

    /// <summary><c>0x141fea780</c>: every 'k' and 'f' slot, column by column. A slot without tag or edges whose corners share
    /// one ground is left for the fill (its entries remember the ground); any other picks a tile from the store:
    /// <c>Next(total weight)</c>.</summary>
    public void Tilise(RoomDef room, int x0, int y0, int o, bool fillGrounds, IReadOnlyList<string> descriptions, int height = 0)
    {
        // Map +0x10 is off while a room is tilised.
        bool check = grid.CheckGrounds;
        grid.CheckGrounds = false;
        TiliseSlots(room, x0, y0, o, fillGrounds, height);
        // 0x141ffbd70: the room's own inner vertex grounds go in unchecked (0x141d85840), turned like a tile's.
        if (room.VertexGrounds.Length > 0)
            for (int v = 1; v < room.Height; v++)
                for (int u = 1; u < room.Width; u++)
                    if (room.VertexGrounds[(v - 1) * (room.Width - 1) + u - 1] is { } g)
                    {
                        var (dx, dy) = Orientation.MapVertex(o, u, v, room.Width, room.Height);
                        grid.ForceGround(x0 + dx + offsetX, y0 + dy + offsetY, g);
                        RoomVertices.Add((x0 + dx + offsetX, y0 + dy + offsetY));
                    }
        grid.CheckGrounds = check;
        var (w, h) = Orientation.Size(o, room.Width, room.Height);
        Rooms.Add(new PlacedRoom(room, x0 + offsetX, y0 + offsetY, w, h, descriptions, o));
    }

    void TiliseSlots(RoomDef room, int x0, int y0, int o, bool fillGrounds, int height)
    {
        for (int col = 0; col < room.Width; col++)
            for (int row = 0; row < room.Height; row++)
            {
                var slot = room.Slots[col, row];
                if (slot is null || slot.Kind is not ('k' or 'f' or 's') || slot.Key is null) continue;
                var (gx, gy) = Orientation.Map(o, col + slot.ShiftX, row + slot.ShiftY, room.Width, room.Height);
                int sw = 1 - slot.Key.Width, sh = 1 - slot.Key.Height, so = Math.Min(o, 7) * 4;
                int x = x0 + gx + sw * SlotShift[so] + sh * SlotShift[so + 2];
                int y = y0 + gy + sw * SlotShift[so + 1] + sh * SlotShift[so + 3];
                var key = slot.Key.Rotate(o);
                if (slot.Tag.Length == 0 && key.Edges.All(e => e is null) && key.Grounds.Distinct().Count() == 1)
                {
                    // The slot's vertices take its ground (as the client loops: w columns, h + 1 rows).
                    if (fillGrounds && key.Grounds[0] is { } ground)
                        for (int v = 0; v <= key.Height; v++)
                            for (int u = 0; u < key.Width; u++)
                                grid.SetGround(x + u + offsetX, y + v + offsetY, ground);
                    continue;
                }
                var list = store.Candidates(slot.Tag, key);
                if (list.Count == 0)
                    throw new NotSupportedException($"{room.Path}: slot ({col}, {row}) {key} tag '{slot.Tag}' has no tile in the tile set (the second store and mission tiles are not ported)");
                int r = (int)rng.Next((uint)list.Sum(c => c.Weight));
                var pick = list[^1];
                foreach (var c in list)
                {
                    if (r < c.Weight) { pick = c; break; }
                    r -= c.Weight;
                }
                grid.Place(pick.Tile, pick.Orientation, x + offsetX, y + offsetY, (short)(slot.Height + height));
                TilePlaced?.Invoke(pick.Tile, pick.Orientation, x, y);
            }
    }
}

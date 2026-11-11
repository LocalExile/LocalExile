using System.Runtime.CompilerServices;

/// <summary>
/// The order the client's entity grid lists objects in (3.29.3c, 11-skills.md §9a): 23-cell tiles, and in each tile a
/// list an object joins at the end of when it enters the tile (0x141d7fb50 inserts after its category's last entry;
/// a tile change from 0x141e32360 removes and adds it, 0x141d878e0), while one leaving keeps the others' order
/// (0x141d7fce0). Its area scans walk the tiles row by row (x fastest) and each tile's list from its start, so their
/// candidates come in that order. The server lists each monster when its step pass first sees it (in the order of
/// conn.Monsters, where it stands) and again whenever a step takes it into another tile (LivingCollision.Step), in the
/// order of the steps; one found in another tile without a step (a teleport) is listed when it is next ordered.
/// </summary>
public static class ClientEntityGrid
{
    const int Tile = 23;

    sealed class Grid
    {
        public int Epoch = int.MinValue;
        public long Next;
        public readonly Dictionary<uint, (int Tx, int Ty, long Order)> Entries = [];
    }

    static readonly ConditionalWeakTable<ClientState, Grid> Grids = new();

    // An area change (ClientState.ProjectileEpoch) starts a new grid.
    static Grid For(ClientState conn)
    {
        var grid = Grids.GetOrCreateValue(conn);
        if (grid.Epoch != conn.ProjectileEpoch)
        {
            grid.Entries.Clear();
            grid.Epoch = conn.ProjectileEpoch;
        }
        return grid;
    }

    /// <summary>Whether the grid lists the object already. Under conn.Monsters.</summary>
    public static bool Lists(ClientState conn, uint id) => For(conn).Entries.ContainsKey(id);

    /// <summary>The object stands on the cell: listed at its tile's end when that is a tile it was not in. Under
    /// conn.Monsters, in the order the client's world moves them.</summary>
    public static void Stepped(ClientState conn, uint id, (int X, int Y) cell) => Enter(For(conn), id, cell);

    static void Enter(Grid grid, uint id, (int X, int Y) cell)
    {
        int tx = cell.X / Tile, ty = cell.Y / Tile;
        if (grid.Entries.TryGetValue(id, out var entry) && entry.Tx == tx && entry.Ty == ty) return;
        grid.Entries[id] = (tx, ty, ++grid.Next);
    }

    /// <summary>The items in the order the client's area scans list them: by tile row, tile column, then entry into the
    /// tile. Under conn.Monsters.</summary>
    public static List<T> Ordered<T>(ClientState conn, IEnumerable<T> items, Func<T, uint> id, Func<T, (int X, int Y)> cell)
    {
        var grid = For(conn);
        var list = items.ToList();
        foreach (var item in list.OrderBy(id)) Enter(grid, id(item), cell(item));
        return [.. list.OrderBy(i => grid.Entries[id(i)].Ty).ThenBy(i => grid.Entries[id(i)].Tx).ThenBy(i => grid.Entries[id(i)].Order)];
    }
}

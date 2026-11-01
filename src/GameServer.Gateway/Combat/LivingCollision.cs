using GameServer.Core.World;

static class LivingCollision
{
    public const int PlayerSize = 1;
    public static int Size(SpawnedMonster m) => m.Variety is { } v ? Math.Clamp(v.ObjectSize - 1, -1, 127) : 2;
    public static bool Blocks(SpawnedMonster m) => !m.Dead && m.Life > 0 && Size(m) >= 0;

    /// <summary>Sweeps every traversed grid cell, so fast actors cannot tunnel through a body between ticks.</summary>
    public static (int X, int Y) Sweep((int X, int Y) from, (int X, int Y) to, int size,
        Func<int, int, bool> floor, IReadOnlyList<ClientPathfinder.Blocker> blockers)
    {
        var last = from;
        foreach (var cell in ClientPathfinder.Line(from, to).Skip(1))
        {
            if (!floor(cell.X, cell.Y) || ClientPathfinder.ObjectBlocks(blockers, cell.X, cell.Y, size + 1, size + 4)) break;
            last = cell;
        }
        return last;
    }
}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delve;

/// <summary>Native terrain windows: the camp entrance is only the first trip, later trips join two mine checkpoints.</summary>
static class DelveTerrain
{
    public static DelveMine.Plan Procedural(DelveMine mine, DelveMine.Plan plan)
    {
        bool horizontal = plan.To.X != plan.From.X;
        int w = horizontal ? Random.Shared.Next(2, 4) : Random.Shared.Next(1, 3);
        int h = horizontal ? Random.Shared.Next(1, 3) : Random.Shared.Next(2, 4);
        var path = new List<(int X, int Y)>();
        if (horizontal)
            for (int x = 0; x < w; x++)
                for (int j = 0; j < h; j++) path.Add((x, (x & 1) == 0 ? j : h - j - 1));
        else
            for (int y = 0; y < h; y++)
                for (int j = 0; j < w; j++) path.Add(((y & 1) == 0 ? j : w - j - 1, y));
        path = path.Select(p => (plan.To.X < plan.From.X ? w - p.X - 1 : p.X,
            plan.To.Y < plan.From.Y ? h - p.Y - 1 : p.Y)).ToList();
        var terrain = new byte[10 + w * h * 5];
        terrain[0] = 0x20; terrain[8] = (byte)(w | h << 4);
        // Native cells read their depth separately from 0x1d's area level. Never send the tutorial's zero depth
        // for a later window. The chart's logical coordinates remain account scoped, independent of this window.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(terrain.AsSpan(4), (ushort)Math.Clamp(plan.Depth - h, 0, 5999 - h));
        byte previousFeature = mine.LastScene?.DestinationFeature ?? 7;
        byte destination = (byte)Random.Shared.Next(10, 21);
        while (destination == previousFeature) destination = (byte)Random.Shared.Next(10, 21);
        for (int i = 0; i < path.Count; i++)
        {
            int at = 10 + (path[i].Y * w + path[i].X) * 5;
            terrain[at] = i == 0 ? previousFeature : i == path.Count - 1 ? destination : (byte)Random.Shared.Next(10, 21);
            if (i == 0) terrain[at + 4] = 1;
            if (i == 0) continue;
            var a = path[i - 1]; var b = path[i];
            int side = b.X > a.X ? 1 : b.X < a.X ? 3 : b.Y > a.Y ? 0 : 2;
            int opposite = (side + 2) % 4, slot = Random.Shared.Next(1, 3);
            int before = 10 + (a.Y * w + a.X) * 5;
            terrain[before + 1] |= (byte)(1 << (side + 4));
            terrain[at + 1] |= (byte)(1 << (opposite + 4));
            terrain[before + 2] |= (byte)(slot << (side * 2));
            terrain[at + 2] |= (byte)((3 - slot) << (opposite * 2));
        }
        uint seed;
        do seed = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(2, int.MaxValue);
        while (seed is 12 or 13 or 14 or 15 || (mine.SceneSeeds ?? []).Contains(seed));
        return plan with { SceneSeed = seed, Terrain = terrain, DestinationFeature = destination };
    }

    public static (int X, int Y)? Anchor(byte[] terrain, bool destination)
    {
        int w = terrain[8] & 15, h = terrain[8] >> 4;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int at = 10 + (y * w + x) * 5;
                bool start = (terrain[at + 4] & 1) != 0;
                if (System.Numerics.BitOperations.PopCount((uint)(terrain[at + 1] >> 4)) == 1 && start != destination)
                    return ((4 + x * 25) * 23, (4 + (h - y - 1) * 25) * 23);
            }
        return null;
    }
    public static uint Seed(DelveMine.Point from, DelveMine.Point to) =>
        from == new DelveMine.Point(2, 0) && to == new DelveMine.Point(2, 1) ? 1u :
        to.X > from.X ? 13u : to.X < from.X ? 15u : to.Y < from.Y ? 14u : 12u;

    public static byte[] Data(uint seed)
    {
        uint direction = seed % 10;
        if (direction is < 2 or > 5) return InstanceDelve.FirstMineTerrain();
        bool vertical = direction is 2 or 4, reverse = direction is 4 or 5;
        byte outward = direction switch { 2 => 0x10, 3 => 0x20, 4 => 0x40, _ => 0x80 };
        byte inward = direction switch { 2 => 0x40, 3 => 0x80, 4 => 0x10, _ => 0x20 };
        byte[] terrain = [0x20, 0, 0, 0, 0, 0, 0, 0, vertical ? (byte)0x21 : (byte)0x12, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        int start = reverse ? 15 : 10, end = reverse ? 10 : 15;
        terrain[start] = 7; terrain[start + 1] = outward; terrain[start + 4] = 1; // completed checkpoint
        terrain[end] = 7; terrain[end + 1] = inward;
        // Each side's 2-bit portal slot is measured from its own corner. Opposite sides need complementary
        // slots (1 + 2 = 3); zero on both sides produces disconnected terrain despite reciprocal connection bits.
        terrain[start + 2] = direction switch { 2 => 1, 3 => 4, 4 => 0x20, _ => 0x80 };
        terrain[end + 2] = direction switch { 2 => 0x20, 3 => 0x80, 4 => 1, _ => 4 };
        return terrain;
    }

    // Native terrain rows run towards -Y; columns run towards +X.
    public static int Progress(uint seed, (int X, int Y) at) => (seed % 10) switch
    { 3 => at.X, 4 => at.Y, 5 => -at.X, _ => -at.Y };
}

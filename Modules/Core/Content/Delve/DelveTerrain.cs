using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delve;

/// <summary>Native terrain windows: the camp entrance is only the first trip, later trips join two mine checkpoints.</summary>
static class DelveTerrain
{
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

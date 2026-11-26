using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The Blight instance protocol messages (tower build C→S 0x0265, resource S→C 0x026a, fast-forward C→S 0x0264,
/// and anoint craft C→S 0x026b). C→S 0x0264 (2 B) and 0x026b (6 B) are already framed in <see cref="ClientPackets"/>.
/// </summary>
public static class InstanceBlight
{
    public const ushort FastForwardId = 0x264, TowerBuildId = 0x265, ResourceId = 0x26a, AnointId = 0x26b;

    /// <summary>Lengths of the fixed C→S Blight messages.</summary>
    public const int FastForwardLength = 2, TowerBuildLength = 10, AnointLength = 6;

    /// <summary>C→S 0x265, 10 bytes: the opcode, a u32 target object (a foundation, or the tower to upgrade) and a u32
    /// BlightTowers row, both big-endian. The row is one of the target's NextUpgradeOptions.</summary>
    public static (uint Target, uint Tower) ReadTowerBuild(ReadOnlySpan<byte> m) => m.Length < TowerBuildLength
        ? throw new ArgumentException($"C→S 0x265 is {TowerBuildLength} bytes, not {m.Length}", nameof(m))
        : (BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

    /// <summary>C→S 0x26b, 6 bytes: opcode + big-endian u32 context/tab/slot word (Cassia window or Blight stash tab).</summary>
    public static uint ReadAnoint(ReadOnlySpan<byte> m) => m.Length < AnointLength
        ? throw new ArgumentException($"C→S 0x26b is {AnointLength} bytes, not {m.Length}", nameof(m))
        : BinaryPrimitives.ReadUInt32BigEndian(m[2..]);

    /// <summary>S→C 0x26a: the resource the client shows and the tower panel spends, as a big-endian u32.</summary>
    public static byte[] Resource(uint total) => new LoginWriter().U16(ResourceId).U32(total).ToArray();
}

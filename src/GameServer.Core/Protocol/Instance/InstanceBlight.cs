using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The tower messages of a Blight pump defence. The client builds and upgrades towers from its own tables and tells the server
/// only its choice (C→S 0x265); the server pays for it from the defence's resource, which S→C 0x26a keeps current.
/// </summary>
public static class InstanceBlight
{
    public const ushort TowerBuildId = 0x265, ResourceId = 0x26a;

    /// <summary>C→S 0x265, 10 bytes: the opcode, a u32 target object (a foundation, or the tower to upgrade) and a u32
    /// BlightTowers row, both big-endian. The row is one of the target's NextUpgradeOptions.</summary>
    public static (uint Target, uint Tower) ReadTowerBuild(ReadOnlySpan<byte> m) =>
        (BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

    /// <summary>S→C 0x26a: the resource the client shows and the tower panel spends, as a big-endian u32.</summary>
    public static byte[] Resource(uint total) => new LoginWriter().U16(ResourceId).U32(total).ToArray();
}

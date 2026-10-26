using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>Native hideout editor: sender 0x142160100, serializer 0x141f93820 (3.29.3b).</summary>
public static class InstanceHideoutEditing
{
    public const ushort MoveId = 0x5b;
    public const int MoveLength = 15;
    public readonly record struct Move(uint ObjectId, ushort X, ushort Y, byte Flags, float Rotation)
    {
        public byte Variation => (byte)(Flags >> 1);
        public bool Mirrored => (Flags & 1) != 0;
    }

    public static Move ReadMove(ReadOnlySpan<byte> m)
    {
        if (m.Length != MoveLength || BinaryPrimitives.ReadUInt16BigEndian(m) != MoveId)
            throw new ArgumentException("Invalid hideout move packet", nameof(m));
        return new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt16BigEndian(m[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(m[8..]), m[10],
            BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(m[11..])));
    }

    /// <summary>Positioned's update reader 0x141e30910: little-endian mask bit 3, then f32 radians.</summary>
    public static byte[] Rotation(uint objectId, float radians) =>
        InstanceEntities.ComponentUpdate(objectId, 0, new BlobWriter().U16(8).F32(radians).ToArray());
}

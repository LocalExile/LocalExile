using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

public static class InstanceHideouts
{
    // HideoutSelectionWindow 0x140bbbb20; serializer 0x141fa3aa0 packs guild in bit15 of the row.
    public const ushort SelectId = 0x254;
    public const int SelectLength = 4;

    public static (int Row, bool Guild) ReadSelect(ReadOnlySpan<byte> message)
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        return (value & 0x7fff, (value & 0x8000) != 0);
    }
}

using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The native NPC divination window.</summary>
public static class InstanceDivination
{
    public const byte InventoryRow = 27, Ui = 0x0c;
    public const ushort TurnInId = 0x19a;
    public const int TurnInLength = 2;
    public const ushort MoveId = 0xea;
    public const int MoveLength = 8;
    public sealed record Move(uint ItemId, bool Expanded, bool IntoWindow);
    public static Move ReadMove(ReadOnlySpan<byte> message) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(message[2..]), message[6] != 0, message[7] != 0);

    public static byte[] Open(uint visual = 0) => InstanceWaypoints.OpenUi(Ui, visual);
}

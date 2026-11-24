using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The Horticrafting Station's craft request (C→S 0x274): the HarvestCraftOptions HASH16 of the selected row.</summary>
public static class InstanceHarvest
{
    // The recipe row's HASH16, e.g. ReforgeFire 0xa3e5; captured live on 2026-10-27.
    public const ushort CraftId = 0x274;
    public const int CraftLength = 4;

    public static ushort ReadCraft(ReadOnlySpan<byte> message) => BinaryPrimitives.ReadUInt16BigEndian(message[2..]);

    /// <summary>C→S 0xf9 (10 bytes, writer 0x141fa0040, sender 0x140312da0): Ctrl + click an item into or out of
    /// HarvestCraftingItem (row 43) while the Horticrafting Station is open (from inventory or stash tab).
    /// u32 BE item id (+2), u8 flags (+6), u16 BE stash tab (+7), u8 intoStation (+9).</summary>
    public const ushort MoveItemId = 0xf9;
    public const int MoveItemLength = 10;

    public readonly record struct MoveItem(uint ItemId, byte Flags, ushort Tab, bool IntoStation);

    public static MoveItem ReadMoveItem(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6], BinaryPrimitives.ReadUInt16BigEndian(m[7..]), m[9] != 0);

    // S→C 0x276 (dispatcher case 0xce): four raw payload bytes whose first u16 is dispatched to the local event list at
    // instance +0x4e70. The Harvest panel looks that item id up in HarvestCraftingItem, finishes the pending craft and
    // refreshes the slot; two unused bytes follow.
    public const ushort CraftResultId = 0x276;
    public const int CraftResultLength = 6;
    public static byte[] CraftResult(ushort itemId = 0) =>
        [CraftResultId >> 8, CraftResultId & 0xff, (byte)(itemId >> 8), (byte)itemId, 0, 0];
}

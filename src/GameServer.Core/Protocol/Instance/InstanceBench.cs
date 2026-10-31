using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

public static class InstanceBench
{
    // 0x140c40720 / 0x141f9b2e0: the high bit is the second button's "Bound Currency Crafting" confirmation
    // (TencentBoundCurrencyCraftingConfirmationTitle); replacing a craft uses the ordinary request.
    public const ushort CraftId = 0x15d;
    public const int CraftLength = 4;
    // Ctrl + click with the bench open (0x14030e3e0 → 0x142167ed0, serializer 0x141f98e80): u32 item id, then a flag byte.
    // 0x80: into the bench (the clicked item is in the player's inventory); clear: out of the bench (row 16) into the
    // inventory. 0x40: the item is in ExpandedMainInventory1 (row 84).
    public const ushort MoveId = 0xe9;
    public const int MoveLength = 7;
    public static (uint ItemId, bool IntoBench) ReadMove(ReadOnlySpan<byte> message) =>
        (BinaryPrimitives.ReadUInt32BigEndian(message[2..]), (message[6] & 0x80) != 0);
    // Currency-count inventory: the bench's native filter (0x140c3b490) accepts kind 0x12,
    // but excludes ordinary stash kind 0x11. Row 26 is deliberately absent from fixed-slot lookup.
    public const uint BalanceInventoryId = 0xfffffe00;
    public static readonly InstanceInventories.Slot BalanceSlot = new(26, 0x12, 255, 255);
    public static (int Recipe, bool Bound) ReadCraft(ReadOnlySpan<byte> message)
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        return (value & 0x7fff, (value & 0x8000) != 0);
    }
}

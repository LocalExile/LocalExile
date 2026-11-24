using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Labyrinth Divine Font crafting messages (S→C <c>0x0246</c>, S→C <c>0x02a2</c>, C→S <c>0x0245</c>/<c>0x0247</c>/<c>0x02a3</c>).
/// </summary>
public static class InstanceLabyrinth
{
    /// <summary>C→S 0x0245 (3 bytes, writer 0x140e27920, sender 0x140c1de40): apply Divine Font craft option index (<c>u8</c>).</summary>
    public const ushort CraftApplyId = 0x0245;
    public const int CraftApplyLength = 3;

    /// <summary>C→S 0x0247 (4 bytes, class 0x1435ca200, writer 0x141f99160, sender 0x140bcb030): select a Divine Font
    /// craft option (<c>u16 BE LabyrinthCraftOptions.HASH16</c>).</summary>
    public const ushort CraftSelectId = 0x0247;
    public const int CraftSelectLength = 4;

    /// <summary>C→S 0x02a3 (3 bytes, writer 0x140e27920, sender 0x140bca390): choose one result gem index (<c>u8</c>)
    /// from the <see cref="CraftResultOptionsList"/> list.</summary>
    public const ushort CraftResultSelectId = 0x02a3;
    public const int CraftResultSelectLength = 3;

    /// <summary>S→C 0x0246 (class 0x1435e6668, read 0x1421b43b0 / write 0x1421b42e0, dispatcher case 0xb8 at 0x142159b0d):
    /// Divine Font craft options list (<c>u8 count</c>, <c>count</c> × <c>u16 BE LabyrinthCraftOptions.HASH16</c>,
    /// <c>u8 remainingUses</c>). Notifies <c>instance+0x5f8</c> listened to by <c>LabyrinthCraftingWindow</c> (<c>0x140bc9a23</c>).</summary>
    public const ushort CraftOptionsId = 0x0246;

    /// <summary>S→C 0x02a2 (class 0x1435ca750, read 0x141fa8860 / write 0x141fa87a0, dispatcher case 0xe0 at 0x142159b44):
    /// <c>InstanceClientLabyrinthCraftResultOptionsList</c> (<c>u64 LE count</c>, then <c>count</c> × <see cref="Item.Wire"/>).
    /// Pairs with <see cref="CraftResultSelectId"/> (<c>C→S 0x02a3</c>).</summary>
    public const ushort CraftResultOptionsListId = 0x02a2;

    public static byte ReadCraftApply(ReadOnlySpan<byte> m) => m[2];

    public static ushort ReadCraftSelect(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadUInt16BigEndian(m[2..]);

    public static byte ReadCraftResultSelect(ReadOnlySpan<byte> m) => m[2];

    public static byte[] CraftOptions(IReadOnlyList<ushort> optionHashes, byte remainingUses)
    {
        int count = Math.Min(optionHashes.Count, byte.MaxValue);
        var w = new LoginWriter().U16(CraftOptionsId).U8((byte)count);
        for (int i = 0; i < count; i++) w.U16(optionHashes[i]);
        return w.U8(remainingUses).ToArray();
    }

    public static byte[] CraftResultOptionsList(IReadOnlyList<Item> options)
    {
        var w = new LoginWriter().U16(CraftResultOptionsListId).RawU64((ulong)options.Count);
        for (int i = 0; i < options.Count; i++) w.Bytes(options[i].Wire());
        return w.ToArray();
    }
}

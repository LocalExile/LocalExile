using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Achievements and Challenges HUD signals (S->C 0x124-0x127; C->S 0x123 is <see cref="InstanceEntities.PanelToggleId"/>).
/// <para>
/// All four S->C messages are dispatched exclusively by the 3.29.3c instance dispatcher (<c>0x142150ae0</c>):
/// <list type="bullet">
/// <item><description><c>0x124</c> (case <c>0x4c</c>, <c>0x1421557f1</c>, Cls <c>0x1435ca200</c>, write <c>0x141f99160</c> / read <c>0x141f991c0</c>): 0-based <c>Achievements.dat</c> row -> instance <c>+0x7df8</c> (completion banner <c>0x1402a4320</c>, panel <c>0x1407fac50</c>, Steam sync <c>0x1401123b0</c>).</description></item>
/// <item><description><c>0x125</c> (case <c>0x4d</c>, <c>0x142155794</c>, Cls <c>0x1435ca200</c>): 0-based <c>AchievementItems.dat</c> row -> instance <c>+0x7d78</c> (item toast <c>0x140dd29a0</c>, panel <c>0x1407fab20</c>).</description></item>
/// <item><description><c>0x126</c> (case <c>0x4e</c>, <c>0x142155774</c>, Cls <c>0x1435e6968</c>, ctor <c>0x1421aca80</c>, write <c>0x1421acac0</c> / read <c>0x1421acb70</c>): per-set progress bitset -> instance <c>+0x7cf8</c> (<c>ChallengesPanel</c> <c>0x1407fa820</c>, secondary panel <c>0x1408037a0</c>, Steam sync <c>0x140112290</c>).</description></item>
/// <item><description><c>0x127</c> (case <c>0x4f</c>, <c>0x14215584e</c>, Cls <c>0x1435ca200</c>): 0-based <c>AchievementItems.dat</c> row -> instance <c>+0x7e78</c> (item failed toast <c>0x140dd2dd0</c>).</description></item>
/// </list>
/// </para>
/// </summary>
public static class InstanceAchievements
{
    public const ushort AchievementCompleteId = 0x124;
    public const ushort AchievementItemCompleteId = 0x125;
    public const ushort AchievementProgressId = 0x126;
    public const ushort AchievementItemFailedId = 0x127;

    /// <summary><c>AchievementSetsDisplay.Id</c> for permanent Achievements (default set in ctor <c>0x1421aca80</c>).</summary>
    public const byte PermanentAchievementSetId = 2;

    /// <summary>
    /// Exact capacity of the client's bitset buffer at <c>msg + 0x16</c> (ctor <c>0x1421aca80</c> zeroes <c>0x100</c>
    /// bytes; object allocation at <c>0x1421acc00</c> is <c>0x118</c> bytes). Reader <c>0x1421acb70</c> copies
    /// <c>rawLen</c> bytes into <c>msg + 0x14</c> without clamping, so <c>bitset.Length</c> must never exceed 256.
    /// </summary>
    public const int MaxBitsetBytes = 256;

    /// <summary>
    /// S->C <c>0x124</c> (4 bytes): <c>u16 BE 0x0124</c>, <c>u16 BE achievementRow</c> (0-based <c>Achievements.dat</c> row,
    /// bounds-checked at <c>0x142361330</c>).
    /// </summary>
    public static byte[] AchievementComplete(ushort achievementRow) =>
        RowMessage(AchievementCompleteId, achievementRow);

    /// <summary>
    /// S->C <c>0x125</c> (4 bytes): <c>u16 BE 0x0125</c>, <c>u16 BE achievementItemRow</c> (0-based <c>AchievementItems.dat</c> row,
    /// bounds-checked at <c>0x14223f970</c>).
    /// </summary>
    public static byte[] AchievementItemComplete(ushort achievementItemRow) =>
        RowMessage(AchievementItemCompleteId, achievementItemRow);

    /// <summary>
    /// S->C <c>0x127</c> (4 bytes): <c>u16 BE 0x0127</c>, <c>u16 BE achievementItemRow</c> (0-based <c>AchievementItems.dat</c> row,
    /// bounds-checked at <c>0x14223f970</c>).
    /// </summary>
    public static byte[] AchievementItemFailed(ushort achievementItemRow) =>
        RowMessage(AchievementItemFailedId, achievementItemRow);

    /// <summary>
    /// S->C <c>0x126</c> (<c>7 + bitset.Length</c> bytes): <c>u16 BE 0x0126</c>, <c>u8 setId</c>,
    /// <c>u16 BE rawLen</c> (<c>bitset.Length + 2</c>), <c>u16 LE bitsetByteCount</c> (<c>bitset.Length</c>),
    /// followed by <paramref name="bitset"/> bytes (write <c>0x1421acac0</c> / read <c>0x1421acb70</c>).
    /// </summary>
    public static byte[] AchievementProgress(byte setId, ReadOnlySpan<byte> bitset)
    {
        if (bitset.Length > MaxBitsetBytes)
            throw new ArgumentOutOfRangeException(nameof(bitset), bitset.Length,
                $"Achievement progress bitset length {bitset.Length} exceeds client buffer capacity of {MaxBitsetBytes} bytes.");
        ushort byteCount = (ushort)bitset.Length;
        ushort rawLen = checked((ushort)(byteCount + 2));
        var buf = new byte[7 + byteCount];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), AchievementProgressId);
        buf[2] = setId;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(3, 2), rawLen);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5, 2), byteCount);
        bitset.CopyTo(buf.AsSpan(7));
        return buf;
    }

    /// <summary>
    /// Packs an unsigned <paramref name="bitWidth"/>-bit value into <paramref name="bitset"/> at
    /// <paramref name="bitOffset"/> using the exact LSB-first, little-endian bit order decoded by
    /// <c>AchievementItems</c> progress checks (3.29.3c <c>0x14220fb20</c> and <c>ChallengesPanel</c> <c>0x1407fa940</c>,
    /// where <c>AchievementItems + 0x08</c> is <c>bitWidth</c> (<c>__col1</c>) and <c>+ 0x0c</c> is <c>bitOffset</c> (<c>__col2</c>)).
    /// </summary>
    public static void WriteBitField(Span<byte> bitset, int bitOffset, int bitWidth, uint value)
    {
        if (bitOffset < 0 || bitWidth <= 0) return;
        int lastByte = (bitOffset + bitWidth - 1) >> 3;
        if ((uint)lastByte >= (uint)bitset.Length)
            throw new ArgumentOutOfRangeException(nameof(bitOffset), bitOffset,
                $"Bit range [{bitOffset}, {bitOffset + bitWidth}) exceeds {bitset.Length}-byte bitset.");
        uint max = bitWidth >= 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        if (value > max) value = max;
        for (int i = 0; i < bitWidth; i++)
        {
            int bit = bitOffset + i;
            int byteIndex = bit >> 3;
            byte mask = (byte)(1 << (bit & 7));
            if (i < 32 && ((value >> i) & 1u) != 0) bitset[byteIndex] |= mask;
            else bitset[byteIndex] &= (byte)~mask;
        }
    }

    static byte[] RowMessage(ushort opcode, ushort row)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), opcode);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), row);
        return buf;
    }
}

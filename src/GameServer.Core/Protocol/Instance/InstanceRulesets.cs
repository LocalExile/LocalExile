using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A ruleset's own messages (Agents/docs/protocol/03-area-and-world.md §8): S→C 0x2c9 carries the ruleset's data and C→S
/// 0x2ca its actions. Both are <c>u16 id, u8 Rulesets row, compact-uint n, n bytes</c>; what the bytes mean belongs to the
/// ruleset's row.
/// </summary>
public static class InstanceRulesets
{
    public const ushort DataId = 0x2c9, ActionId = 0x2ca;

    /// <summary>S→C 0x2c9: data for the ruleset of this Rulesets row.</summary>
    public static byte[] Data(byte row, ReadOnlySpan<byte> payload)
    {
        var w = new LoginWriter().U16(DataId).U8(row);
        InstanceProtocol.CompactUInt(w, (uint)payload.Length);
        foreach (byte b in payload) w.U8(b);
        return w.ToArray();
    }

    /// <summary>The length of a C→S 0x2ca at the start of <paramref name="m"/>, or null while it is cut off.</summary>
    public static int? ActionLength(ReadOnlySpan<byte> m)
    {
        if (m.Length < 4) return null;
        int lengthBytes = CompactUIntLength(m[3]);
        if (m.Length < 3 + lengthBytes) return null;
        long total = 3L + lengthBytes + CompactUInt(m[3..]);
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>C→S 0x2ca: the Rulesets row and the action's bytes.</summary>
    public static (byte Row, byte[] Payload) ReadAction(ReadOnlySpan<byte> m)
    {
        if (m.Length < 4 || (m[0] << 8 | m[1]) != ActionId) throw new InvalidDataException("not a 0x2ca ruleset action");
        int lengthBytes = CompactUIntLength(m[3]);
        uint n = CompactUInt(m[3..]);
        if (m.Length < 3 + lengthBytes + n) throw new InvalidDataException("0x2ca ruleset action is cut off");
        return (m[2], m.Slice(3 + lengthBytes, (int)n).ToArray());
    }

    static int CompactUIntLength(byte first) => first < 0x80 ? 1 : first < 0xc0 ? 2 : first < 0xe0 ? 3 : first < 0xf0 ? 4 : 5;

    static uint CompactUInt(ReadOnlySpan<byte> b) => CompactUIntLength(b[0]) switch
    {
        1 => b[0],
        2 => (uint)((b[0] & 0x3f) << 8 | b[1]),
        3 => (uint)((b[0] & 0x1f) << 16 | b[1] << 8 | b[2]),
        4 => (uint)((b[0] & 0x0f) << 24 | b[1] << 16 | b[2] << 8 | b[3]),
        _ => (uint)(b[1] << 24 | b[2] << 16 | b[3] << 8 | b[4]),
    };
}

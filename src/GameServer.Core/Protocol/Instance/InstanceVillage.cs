using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// C→S 0x2ca: a Kingsmarch village panel's button press. One channel carries every panel (town map, recruitment,
/// farm, port).
///
/// The client's writer (0x141fa7c10, the message class 0x1435cb1d0) puts the record kind of the panel first (0x9b for
/// the town, 0x74 for the farm), then the action's payload as a compact-int-prefixed blob: the wire is
/// <c>u16 id | u8 kind | compact-int length | blob</c>. Live capture (2026-11-22, the town panel's buttons):
/// <c>02CA9B020000</c> — id 0x2ca, kind 0x9b, a two-byte blob <c>00 00</c>.
///
/// Because the length is on the wire, the Gateway can frame this message without knowing the action; before this,
/// 0x2ca was not in the client's known lengths and every button press landed in the "unframed" bytes, so no module
/// could see it.
/// </summary>
public static class InstanceVillage
{
    /// <summary>C→S 0x2ca: the village panel action channel.</summary>
    public const ushort ActionId = 0x2ca;

    /// <summary>The record kind of the town panel (the client's <c>ClientVillage</c> record 0x9b); the farm panel uses 0x74.</summary>
    public const byte TownKind = 0x9b, FarmKind = 0x74;

    /// <summary>The length of a C→S 0x2ca at the start of <paramref name="data"/>, or null until the compact length arrived.</summary>
    public static int? ActionLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        if (CompactLength(data, 3) is not (int len, int size)) return null;
        return 3 + size + len;
    }

    /// <summary>The record kind the action came from (0x9b town, 0x74 farm).</summary>
    public static byte Kind(ReadOnlySpan<byte> message) => message.Length > 2 ? message[2] : (byte)0;

    /// <summary>The action blob: the action byte and its arguments, as the client wrote them.</summary>
    public static ReadOnlySpan<byte> Blob(ReadOnlySpan<byte> message)
    {
        if (message.Length < 4) return [];
        if (CompactLength(message, 3) is not (int len, int size)) return [];
        int start = 3 + size;
        return message.Length >= start + len ? message.Slice(start, len) : [];
    }

    /// <summary>The compact int at <paramref name="at"/>: its value and how many bytes it took (the game's own
    /// variable-length int, 0x14089f790: one byte below 0x80, then 0x80/0xc0/0xe0/0xf0 prefixes).</summary>
    public static (int Value, int Size)? CompactLength(ReadOnlySpan<byte> data, int at)
    {
        if (data.Length <= at) return null;
        byte first = data[at];
        if (first < 0x80) return (first, 1);
        if (first < 0xc0)
        {
            if (data.Length <= at + 1) return null;
            return (((first & 0x3f) << 8) | data[at + 1], 2);
        }
        if (first < 0xe0)
        {
            if (data.Length <= at + 2) return null;
            return (((first & 0x1f) << 16) | (data[at + 1] << 8) | data[at + 2], 3);
        }
        if (first < 0xf0)
        {
            if (data.Length <= at + 3) return null;
            return (((first & 0x0f) << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3], 4);
        }
        if (data.Length <= at + 4) return null;
        return ((data[at + 1] << 24) | (data[at + 2] << 16) | (data[at + 3] << 8) | data[at + 4], 5);
    }

    /// <summary>The blob's first byte: the action.</summary>
    public static byte Action(ReadOnlySpan<byte> message) => Blob(message).Length > 0 ? Blob(message)[0] : (byte)0;

    /// <summary>The blob's second byte, as a u8 argument (a worker, a row, a tier).</summary>
    public static byte Arg8(ReadOnlySpan<byte> message) => Blob(message).Length > 1 ? Blob(message)[1] : (byte)0;

    /// <summary>The blob's first argument as a u16 (the treasury buttons).</summary>
    public static ushort Arg16(ReadOnlySpan<byte> message)
    {
        var blob = Blob(message);
        return blob.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(blob) : (ushort)0;
    }

    /// <summary>The blob's first argument as a u32 (gold amounts).</summary>
    public static uint Arg32(ReadOnlySpan<byte> message)
    {
        var blob = Blob(message);
        return blob.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(blob) : 0;
    }
}

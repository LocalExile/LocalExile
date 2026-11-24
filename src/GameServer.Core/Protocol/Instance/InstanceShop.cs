using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Local offline microtransaction shop, mystery/salvage box opening, and JSON backend error protocol
/// (Path of Exile 3.29.3c): <c>C→S 0x012c/0x012e/0x0130/0x0138/0x0206/0x026e</c> and
/// <c>S→C 0x012d/0x0131/0x0132/0x0133/0x0139/0x0282</c>.
/// </summary>
public static class InstanceShop
{
    /// <summary>C→S 0x012c (writer <c>0x141fa1160</c>): local shop purchase request
    /// (<c>WStr packageId</c>, <c>WStr extra</c>, <c>u32 BE quantityOrPrice</c>, <c>u8 flags</c>).</summary>
    public const ushort BuyId = 0x012c;

    /// <summary>S→C 0x012d (reader <c>0x1421a46b0</c>, dispatcher case <c>0x52</c>): shop purchase result
    /// (<c>u16 BE backendErrorCode</c>, <c>WStr message</c>).</summary>
    public const ushort BuyResultId = 0x012d;

    /// <summary>C→S 0x012e (2 bytes, writer <c>0x140911160</c>): shop/box inventory reload request.</summary>
    public const ushort ReloadId = 0x012e;
    public const int ReloadLength = 2;

    /// <summary>C→S 0x0130 (writer <c>0x141fa1600</c>): shipping quote request (4 narrow UTF-8 strings).</summary>
    public const ushort ShippingQuoteRequestId = 0x0130;

    /// <summary>S→C 0x0131 (reader <c>0x141fa44a0</c>, dispatcher case <c>0x53</c>): account shop points balance
    /// (<c>u32 BE points</c>, <c>u32 BE guildPoints</c>, <c>u32 BE u18</c>, <c>u32 BE u1c</c>).</summary>
    public const ushort PointsId = 0x0131;

    /// <summary>S→C 0x0132 (reader <c>0x1421ad4a0</c>, dispatcher case <c>0x54</c> at <c>0x142155d44</c>,
    /// decompressor <c>0x1421490a0</c> → <c>0x140e44ff0</c>): <c>ShopPaymentPackagesResult</c>
    /// (<c>u16 BE statusCode</c>, <c>u16 BE blobLen</c>, <c>byte[blobLen]</c> LZ4-compressed JSON envelope).</summary>
    public const ushort PaymentPackagesResultId = 0x0132;

    /// <summary>S→C 0x0133 (reader <c>0x1421aae00</c>, dispatcher case <c>0x55</c> at <c>0x142155da4</c>):
    /// shipping quote result (<c>u16 BE backendErrorCode</c>, <c>u16 BE utf8Len</c>, <c>byte[utf8Len]</c> JSON).</summary>
    public const ushort ShippingQuoteId = 0x0133;

    /// <summary>C→S 0x0138 (writer <c>0x141fa1600</c>): shop order request (4 narrow UTF-8 strings).</summary>
    public const ushort PlaceOrderId = 0x0138;

    /// <summary>S→C 0x0139 (reader <c>0x141f991c0</c>, dispatcher case <c>0x58</c>): shop order result
    /// (<c>u16 BE backendErrorCode</c>).</summary>
    public const ushort OrderResultId = 0x0139;

    /// <summary>C→S 0x0206 (6 bytes, writer <c>0x140911210</c>, sender <c>0x140c0ba80</c>): open mystery box
    /// (<c>u32 BE recordId</c>).</summary>
    public const ushort OpenMysteryBoxId = 0x0206;
    public const int OpenMysteryBoxLength = 6;

    /// <summary>C→S 0x026e (writer <c>0x141fa3bf0</c> → <c>0x141faa4a0</c>): open salvage box
    /// (<c>u32 BE boxRecordId</c>, <c>u16 BE count</c>, <c>count × u32 BE consumedRecordIds</c>).</summary>
    public const ushort OpenSalvageBoxId = 0x026e;

    /// <summary>S→C 0x0282 (reader <c>0x141fa6350</c>, dispatcher case <c>0xd2</c> at <c>0x14215a9a5</c>):
    /// JSON backend error payload (<c>u16 BE utf8Len</c>, <c>byte[utf8Len]</c> UTF-8 JSON).</summary>
    public const ushort JsonErrorId = 0x0282;

    /// <summary>Parsed <see cref="BuyId"/> request.</summary>
    public sealed record BuyRequest(string PackageId, string Extra, uint QuantityOrPrice, byte Flags);

    /// <summary>Parsed 4-string payload of <see cref="ShippingQuoteRequestId"/> or <see cref="PlaceOrderId"/>.</summary>
    public sealed record FourStringsRequest(string Field0, string Field1, string Field2, string Field3);

    /// <summary>Parsed <see cref="OpenSalvageBoxId"/> request.</summary>
    public sealed record OpenSalvageBoxRequest(uint BoxRecordId, IReadOnlyList<uint> ConsumedRecordIds);

    public static BuyRequest ReadBuy(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.WStr(), r.WStr(), r.U32(), r.U8());
    }

    public static FourStringsRequest ReadFourStrings(ReadOnlySpan<byte> m)
    {
        int pos = 2;
        string s0 = ReadNarrowStr(m, ref pos);
        string s1 = ReadNarrowStr(m, ref pos);
        string s2 = ReadNarrowStr(m, ref pos);
        string s3 = ReadNarrowStr(m, ref pos);
        return new(s0, s1, s2, s3);
    }

    public static uint ReadOpenMysteryBox(ReadOnlySpan<byte> m)
    {
        if (m.Length < OpenMysteryBoxLength)
            throw new InvalidDataException($"0x0206 is {m.Length} bytes, not {OpenMysteryBoxLength}");
        return BinaryPrimitives.ReadUInt32BigEndian(m[2..]);
    }

    public static OpenSalvageBoxRequest ReadOpenSalvageBox(ReadOnlySpan<byte> m)
    {
        if (m.Length < 8) throw new InvalidDataException($"0x026e is {m.Length} bytes, too short");
        uint boxId = BinaryPrimitives.ReadUInt32BigEndian(m[2..]);
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        var consumed = new uint[count];
        for (int i = 0, at = 8; i < count; i++, at += 4)
            consumed[i] = BinaryPrimitives.ReadUInt32BigEndian(m[at..]);
        return new(boxId, consumed);
    }

    public static byte[] EncodeBuyResult(ushort backendErrorCode = 0, string message = "") =>
        new LoginWriter().U16(BuyResultId).U16(backendErrorCode).WStr(message).ToArray();

    public static byte[] EncodePoints(uint points, uint guildPoints = 0, uint u18 = 0, uint u1c = 0) =>
        new LoginWriter().U16(PointsId).U32(points).U32(guildPoints).U32(u18).U32(u1c).ToArray();

    public static byte[] EncodeOrderResult(ushort backendErrorCode = 0) =>
        new LoginWriter().U16(OrderResultId).U16(backendErrorCode).ToArray();

    /// <summary>Encodes S→C 0x0133 (shipping quote JSON response).</summary>
    public static byte[] EncodeShippingQuote(ushort backendErrorCode, string json)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        if (utf8.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(json));
        return new LoginWriter().U16(ShippingQuoteId).U16(backendErrorCode).U16((ushort)utf8.Length).Bytes(utf8).ToArray();
    }

    /// <summary>Encodes S→C 0x0282 (raw UTF-8 JSON backend error payload).</summary>
    public static byte[] EncodeJsonError(string json)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        if (utf8.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(json));
        return new LoginWriter().U16(JsonErrorId).U16((ushort)utf8.Length).Bytes(utf8).ToArray();
    }

    /// <summary>Encodes S→C 0x0282 with the client's standard <c>{"error":{"message":...,"fields":[...]}}</c> object.</summary>
    public static byte[] EncodeJsonErrorMessage(string message, IReadOnlyList<string>? fields = null)
    {
        string json = JsonSerializer.Serialize(new { error = new { message, fields = fields ?? [] } });
        return EncodeJsonError(json);
    }

    /// <summary>
    /// Encodes S→C 0x0132 (<c>ShopPaymentPackagesResult</c>). Matches <c>0x1421490a0</c>: when <paramref name="json"/>
    /// is empty the blob length is 0; otherwise the blob is <c>u32 LE uncompressedSize</c> followed by an LZ4 raw block
    /// compressing <c>u32 LE utf8Len || utf8</c>.
    /// </summary>
    public static byte[] EncodePaymentPackagesResult(ushort statusCode, string json)
    {
        if (string.IsNullOrEmpty(json))
            return new LoginWriter().U16(PaymentPackagesResultId).U16(statusCode).U16(0).ToArray();

        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        byte[] inner = new byte[4 + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(inner, (uint)utf8.Length);
        utf8.CopyTo(inner.AsSpan(4));

        byte[] compressed = CompressLz4RawBlock(inner);
        int blobLen = 4 + compressed.Length;
        if (blobLen > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(json), $"0x0132 compressed blob ({blobLen} bytes) exceeds u16");

        var w = new LoginWriter().U16(PaymentPackagesResultId).U16(statusCode).U16((ushort)blobLen);
        w.RawU32((uint)inner.Length).Bytes(compressed);
        return w.ToArray();
    }

    /// <summary>
    /// Pure-C# LZ4 raw block compressor compatible with <c>LZ4_decompress_safe</c> (<c>0x140e44ff0</c>). Uses a 4-byte
    /// hash table for matches while honoring LZ4's 12-byte MFLIMIT and 5-byte trailing literals invariant.
    /// </summary>
    public static byte[] CompressLz4RawBlock(ReadOnlySpan<byte> src)
    {
        if (src.IsEmpty) return [];
        const int MinMatch = 4, MfLimit = 12, LastLiterals = 5, HashBits = 12, HashSize = 1 << HashBits;
        var output = new List<byte>(src.Length + (src.Length / 255) + 16);
        int[] table = new int[HashSize];
        Array.Fill(table, -1);

        int anchor = 0, ip = 0;
        int matchLimit = src.Length - LastLiterals;
        int mflimitPos = src.Length - MfLimit;

        if (src.Length >= MfLimit)
        {
            while (ip <= mflimitPos)
            {
                uint seq = BinaryPrimitives.ReadUInt32LittleEndian(src[ip..]);
                int h = (int)((seq * 2654435761u) >> (32 - HashBits));
                int refPos = table[h];
                table[h] = ip;

                if (refPos >= 0 && ip - refPos <= 0xffff && BinaryPrimitives.ReadUInt32LittleEndian(src[refPos..]) == seq)
                {
                    int matchLen = MinMatch;
                    while (ip + matchLen < matchLimit && src[refPos + matchLen] == src[ip + matchLen])
                        matchLen++;

                    int litLen = ip - anchor;
                    int matchCode = matchLen - MinMatch;
                    byte token = (byte)((Math.Min(litLen, 15) << 4) | Math.Min(matchCode, 15));
                    output.Add(token);
                    WriteExtendedLength(output, litLen);
                    for (int i = 0; i < litLen; i++) output.Add(src[anchor + i]);

                    int offset = ip - refPos;
                    output.Add((byte)(offset & 0xff));
                    output.Add((byte)((offset >> 8) & 0xff));
                    WriteExtendedLength(output, matchCode);

                    ip += matchLen;
                    anchor = ip;
                    continue;
                }
                ip++;
            }
        }

        // Emit remaining bytes as final literals-only sequence (matchCode = 0, no offset).
        int finalLit = src.Length - anchor;
        output.Add((byte)(Math.Min(finalLit, 15) << 4));
        WriteExtendedLength(output, finalLit);
        for (int i = anchor; i < src.Length; i++) output.Add(src[i]);
        return [.. output];
    }

    /// <summary>Decodes an LZ4 raw block into <paramref name="uncompressedLength"/> bytes (for verification/tests).</summary>
    public static byte[] DecompressLz4RawBlock(ReadOnlySpan<byte> src, int uncompressedLength)
    {
        if (uncompressedLength == 0) return [];
        byte[] dst = new byte[uncompressedLength];
        int ip = 0, op = 0;
        while (ip < src.Length)
        {
            byte token = src[ip++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                byte b;
                do { b = src[ip++]; litLen += b; } while (b == 255);
            }
            src.Slice(ip, litLen).CopyTo(dst.AsSpan(op, litLen));
            ip += litLen;
            op += litLen;
            if (ip >= src.Length) break;

            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            if (offset <= 0 || offset > op) throw new InvalidDataException($"Invalid LZ4 match offset {offset}");

            int matchLen = (token & 0x0f) + 4;
            if ((token & 0x0f) == 15)
            {
                byte b;
                do { b = src[ip++]; matchLen += b; } while (b == 255);
            }
            int matchPos = op - offset;
            for (int i = 0; i < matchLen; i++) dst[op++] = dst[matchPos + i];
        }
        if (op != uncompressedLength)
            throw new InvalidDataException($"LZ4 decompressed {op} bytes, expected {uncompressedLength}");
        return dst;
    }

    static void WriteExtendedLength(List<byte> output, int len)
    {
        if (len < 15) return;
        int rem = len - 15;
        while (rem >= 255) { output.Add(255); rem -= 255; }
        output.Add((byte)rem);
    }

    static string ReadNarrowStr(ReadOnlySpan<byte> m, ref int pos)
    {
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
        pos += 2;
        string s = Encoding.UTF8.GetString(m.Slice(pos, len));
        pos += len;
        return s;
    }
}

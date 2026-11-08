using System.Buffers.Binary;
using System.Text;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Writes login-connection fields the way the client's stream (send 0x140dfa000) does: integers big-endian through
/// htons/htonl, except where a serializer copies memory as-is (<see cref="RawU32"/>, <see cref="RawU64"/>).
/// </summary>
public sealed class LoginWriter
{
    private readonly List<byte> _b = [];

    public int Length => _b.Count;
    public byte[] ToArray() => [.. _b];

    public LoginWriter U8(byte v) { _b.Add(v); return this; }
    public LoginWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public LoginWriter U16(ushort v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); return this; }
    public LoginWriter U32(uint v) { U16((ushort)(v >> 16)); return U16((ushort)v); }

    /// <summary>A 4-byte field copied from memory without htonl (little-endian on the wire).</summary>
    public LoginWriter RawU32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); return Bytes(s); }

    /// <summary>An 8-byte field copied from memory without byte swapping (little-endian on the wire).</summary>
    public LoginWriter RawU64(ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, v); return Bytes(s); }

    public LoginWriter Bytes(ReadOnlySpan<byte> v) { foreach (byte x in v) _b.Add(x); return this; }

    /// <summary>A fixed-size field (digests, ids): exactly <paramref name="size"/> bytes.</summary>
    public LoginWriter Fixed(byte[] v, int size)
    {
        if (v.Length != size) throw new ArgumentException($"field must be {size} bytes, got {v.Length}");
        return Bytes(v);
    }

    /// <summary>u16 length + bytes (client 0x14083f460 / 0x141faaf20).</summary>
    public LoginWriter Blob(byte[] v) => U16(checked((ushort)v.Length)).Bytes(v);

    /// <summary>A narrow std::string as u16 length + bytes; the client converts with UTF-8.</summary>
    public LoginWriter Str(string v) => Blob(Encoding.UTF8.GetBytes(v));

    /// <summary>
    /// A wide string in the compact form of client 0x140e0b250: u16 count of code points, then per code point either
    /// one byte (&lt; 0x80), 0x80 + the UTF-16 unit little-endian, or 0xC0 + the code point as u32 little-endian.
    /// Lone surrogates go out as 0x80 + U+FFFD, as the client does.
    /// </summary>
    public LoginWriter WStr(string v)
    {
        var cps = new List<int>(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (char.IsHighSurrogate(c) && i + 1 < v.Length && char.IsLowSurrogate(v[i + 1])) cps.Add(char.ConvertToUtf32(c, v[++i]));
            else cps.Add(char.IsSurrogate(c) ? 0xFFFD : c);
        }
        U16(checked((ushort)cps.Count));
        foreach (int cp in cps)
        {
            if (cp < 0x80) U8((byte)cp);
            else if (cp < 0x10000) { U8(0x80); U8((byte)cp); U8((byte)(cp >> 8)); }
            else { U8(0xC0); RawU32((uint)cp); }
        }
        return this;
    }
}

/// <summary>
/// Reads login-connection fields. Like the client's receive stream (0x140df9ed0, flag +0x252), running out of data
/// does not throw: <see cref="Short"/> is set and defaults are returned, so the caller can wait for more bytes.
/// Malformed data (bad wide-string markers) throws <see cref="InvalidDataException"/>.
/// </summary>
public ref struct LoginReader(ReadOnlySpan<byte> buf)
{
    private readonly ReadOnlySpan<byte> _buf = buf;

    public int Position { get; private set; }
    public bool Short { get; private set; }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Short || _buf.Length - Position < n) { Short = true; return default; }
        var s = _buf.Slice(Position, n);
        Position += n;
        return s;
    }

    public byte U8() { var s = Take(1); return Short ? (byte)0 : s[0]; }
    public bool Bool() => U8() != 0;
    public ushort U16() { var s = Take(2); return Short ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(s); }
    public uint U32() { var s = Take(4); return Short ? 0 : BinaryPrimitives.ReadUInt32BigEndian(s); }
    public uint RawU32() { var s = Take(4); return Short ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(s); }
    public ulong RawU64() { var s = Take(8); return Short ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(s); }
    public byte[] Fixed(int n) { var s = Take(n); return Short ? new byte[n] : s.ToArray(); }
    public byte[] Blob() { int n = U16(); return Short ? [] : Fixed(n); }
    public string Str() => Encoding.UTF8.GetString(Blob());

    /// <summary>Inverse of <see cref="LoginWriter.WStr"/>, with the client's checks from 0x140e0b480.</summary>
    public string WStr()
    {
        int count = U16();
        var sb = new StringBuilder(count);
        for (int i = 0; i < count && !Short; i++)
        {
            byte tag = U8();
            if (Short) break;
            if (tag < 0x80) sb.Append((char)tag);
            else if (tag == 0x80)
            {
                var s = Take(2);
                if (Short) break;
                ushort u = BinaryPrimitives.ReadUInt16LittleEndian(s);
                if (u is >= 0xD800 and < 0xE000) throw new InvalidDataException("Protocol violation reading wstring from the network");
                sb.Append((char)u);
            }
            else if (tag == 0xC0)
            {
                uint cp = RawU32();
                if (Short) break;
                if (cp is >= 0xD800 and < 0xDC00)
                {
                    // A high surrogate sent as its own 0xC0 element; the client pairs it with a following 0xC0 low surrogate.
                    int save = Position;
                    if (i + 1 < count && U8() == 0xC0 && RawU32() is uint lo and >= 0xDC00 and < 0xE000 && !Short)
                    {
                        sb.Append((char)cp).Append((char)lo);
                        i++;
                    }
                    else if (Short) break;
                    else { Position = save; sb.Append('�'); }
                }
                else if (cp is >= 0xDC00 and < 0xE000 || cp > 0x10FFFF) sb.Append('�');
                else if (cp < 0x10000) throw new InvalidDataException("Protocol violation reading wstring from the network");
                else sb.Append(char.ConvertFromUtf32((int)cp));
            }
            else throw new InvalidDataException("Protocol violation reading wstring from the network");
        }
        return Short ? "" : sb.ToString();
    }
}

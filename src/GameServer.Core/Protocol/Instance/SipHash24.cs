using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// SipHash-2-4 (128-bit key, 64-bit output). Used by the 3.29.3c client (<c>0x14214ee50</c> → <c>0x141fe7d90</c>)
/// to authenticate the plaintext C→S 0x0E instance opening against login 0x14 / instance 0x27 <c>Data8</c> and
/// the client's Unix timestamp.
/// </summary>
public static class SipHash24
{
    /// <summary>Computes the 64-bit SipHash-2-4 tag for <paramref name="message"/> under <paramref name="key16"/>.</summary>
    public static ulong Compute(ReadOnlySpan<byte> key16, ReadOnlySpan<byte> message)
    {
        if (key16.Length != 16)
            throw new ArgumentException("SipHash-2-4 requires a 16-byte key.", nameof(key16));

        ulong k0 = BinaryPrimitives.ReadUInt64LittleEndian(key16);
        ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(key16[8..]);

        ulong v0 = 0x736f6d6570736575UL ^ k0;
        ulong v1 = 0x646f72616e646f6dUL ^ k1;
        ulong v2 = 0x6c7967656e657261UL ^ k0;
        ulong v3 = 0x7465646279746573UL ^ k1;

        int fullBlocks = message.Length & ~7;
        for (int i = 0; i < fullBlocks; i += 8)
        {
            ulong m = BinaryPrimitives.ReadUInt64LittleEndian(message[i..]);
            v3 ^= m;
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        ulong b = ((ulong)message.Length & 0xff) << 56;
        var tail = message[fullBlocks..];
        switch (tail.Length)
        {
            case 7: b |= (ulong)tail[6] << 48; goto case 6;
            case 6: b |= (ulong)tail[5] << 40; goto case 5;
            case 5: b |= (ulong)tail[4] << 32; goto case 4;
            case 4: b |= (ulong)tail[3] << 24; goto case 3;
            case 3: b |= (ulong)tail[2] << 16; goto case 2;
            case 2: b |= (ulong)tail[1] << 8; goto case 1;
            case 1: b |= tail[0]; break;
        }

        v3 ^= b;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;

        v2 ^= 0xff;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);

        return v0 ^ v1 ^ v2 ^ v3;
    }

    /// <summary>Computes the 64-bit SipHash-2-4 tag and writes it in little-endian order to <paramref name="destination"/>.</summary>
    public static void ComputeBytes(ReadOnlySpan<byte> key16, ReadOnlySpan<byte> message, Span<byte> destination)
    {
        if (destination.Length < 8)
            throw new ArgumentException("SipHash-2-4 output requires at least 8 bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Compute(key16, message));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void SipRound(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
        v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
        v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
        v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
    }
}

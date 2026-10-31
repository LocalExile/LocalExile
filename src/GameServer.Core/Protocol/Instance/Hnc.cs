using System.Numerics;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// "HNC", the instance connection's hidden side channel (stream +0xd0). Some message fields are carriers: their bits
/// are fed, LSB first, into a byte accumulator, and every completed byte is decrypted with a second Salsa20 pair and
/// run through a small state machine (0x14299de50): idle while bytes decrypt to 0; otherwise a type byte (0xDA–0xDD),
/// a length byte (not 0) and that many payload bytes form a packet. Any other byte throws "HNC Protocol Violated".
/// The pair is keyed like the main stream, from km' = the 0x14 key XOR a 64-byte table in the client (0x142fe0da0) with
/// six byte swaps (0x14299db60), built when 0x11 arrives.
/// </summary>
public sealed class HncSender
{
    /// <summary>VA of the 64-byte mask in the 3.29.3b client, and the SHA-256 of its contents (the mask itself is
    /// extracted from the local client, not shipped).</summary>
    public const long MaskVa = 0x142fe0da0;
    private const string MaskSha256 = "0e88f29acc9cab88ee400ca78bbfeeeaebc333afb11f1ea9be3fd37aeb7cbc31";

    private static byte[]? _mask;

    private static byte[] Mask => _mask ?? throw new InvalidOperationException(
        "HNC mask not loaded: call HncSender.UseMask with the extracted mask (data/client-constants.json).");

    /// <summary>Reads the mask from the client executable (PE file) and checks it against the known hash.</summary>
    public static void LoadMask(byte[] clientExe) => UseMask(PeImage.ReadAtVa(clientExe, MaskVa, 64));

    /// <summary>Uses a mask extracted from the client (data/client-constants.json), checked against the known hash.</summary>
    public static void UseMask(byte[] mask)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(mask)).ToLowerInvariant();
        if (hash != MaskSha256)
            throw new InvalidDataException("HNC mask not found: the client is not the expected 3.29.3b build.");
        _mask = mask;
    }

    /// <summary>Tests only: any 64 bytes (both sides derive with the same mask).</summary>
    public static void UseMaskForTests(byte[] mask) => _mask = mask.Length == 64 ? mask : throw new ArgumentException("64 bytes");

    private readonly Salsa20 _send;

    /// <param name="instanceKey">The 64 key bytes sent in login 0x14.</param>
    public HncSender(ReadOnlySpan<byte> instanceKey) => _send = Pair(instanceKey, isClient: false).Send;

    public static byte[] DeriveKey(ReadOnlySpan<byte> instanceKey)
    {
        if (instanceKey.Length != 64) throw new ArgumentException("instance key must be 64 bytes", nameof(instanceKey));
        byte[] k = new byte[64];
        for (int i = 0; i < 64; i++) k[i] = (byte)(instanceKey[i] ^ Mask[i]);
        foreach (var (a, b) in new[] { (0, 0x15), (4, 2), (0xd, 0x2a), (0x30, 1), (0x1d, 0x1e), (0x32, 0x13) })
            (k[a], k[b]) = (k[b], k[a]);
        return k;
    }

    /// <summary>The HNC Salsa20 pair for one side (the client builds it with isClient = 1).</summary>
    public static ConnectionCiphers Pair(ReadOnlySpan<byte> instanceKey, bool isClient) => new(DeriveKey(instanceKey), isClient);

    /// <summary>
    /// The u32 for a 0x1c carrying four idle (zero) HNC bytes. The reader (0x141fa0720) feeds bits 9..31 and then
    /// bits 0..8 into the accumulator, so the byte stream r (little-endian) is rotr(v, 9) and v = rotl(r, 9).
    /// </summary>
    public uint NextIdleU32()
    {
        Span<byte> b = stackalloc byte[4];
        b.Clear();
        _send.Process(b);
        return BitOperations.RotateLeft(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b), 9);
    }

    /// <summary>The HNC bytes the client would see for carrier u32 <paramref name="v"/> of a 0x1c (before decryption).</summary>
    public static byte[] CarrierBytes(uint v)
    {
        byte[] r = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(r, BitOperations.RotateRight(v, 9));
        return r;
    }
}

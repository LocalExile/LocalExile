using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Message 0x0002 (one class both ways, vtable 0x1435caa70, (de)serializer 0x141fa4220), big-endian:
/// u16 id=2, u16 n, n bytes "key", u16 m, m bytes "signature". Client→server: its ephemeral DH public value and an
/// empty signature. Server→client: the server's ephemeral public value and DSA/SHA-1(key) as r||s. Both are plaintext;
/// every byte after them is Salsa20-encrypted.
/// </summary>
public sealed record KeyExchange(byte[] Key, byte[] Signature) : ILoginMessage
{
    public const ushort Id = 0x0002;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.Blob(Key).Blob(Signature);

    public byte[] Encode()
    {
        byte[] b = new byte[6 + Key.Length + Signature.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, Id);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), checked((ushort)Key.Length));
        Key.CopyTo(b, 4);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4 + Key.Length), checked((ushort)Signature.Length));
        Signature.CopyTo(b, 6 + Key.Length);
        return b;
    }

    /// <summary>Parses one message from the start of <paramref name="buf"/>; false if more bytes are needed.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> buf, out KeyExchange? message, out int consumed)
    {
        message = null; consumed = 0;
        if (buf.Length < 4) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(buf) != Id)
            throw new InvalidDataException($"expected message 0x0002, got 0x{BinaryPrimitives.ReadUInt16BigEndian(buf):x4}");
        int n = BinaryPrimitives.ReadUInt16BigEndian(buf[2..]);
        if (buf.Length < 6 + n) return false;
        int m = BinaryPrimitives.ReadUInt16BigEndian(buf[(4 + n)..]);
        if (buf.Length < 6 + n + m) return false;
        message = new KeyExchange(buf.Slice(4, n).ToArray(), buf.Slice(6 + n, m).ToArray());
        consumed = 6 + n + m;
        return true;
    }
}

/// <summary>Server side of the login key exchange (inverse of client 0x142144f60 / 0x140e01ed0).</summary>
public sealed class ServerHandshake(LoginServerKeys keys)
{
    /// <summary>Answers a client's 0x0002; returns the reply to send in plaintext and the ciphers for everything after it.</summary>
    public (byte[] Reply, ConnectionCiphers Ciphers) Respond(KeyExchange hello)
    {
        DhGroup eph = LoginGroups.Ephemeral, stat = LoginGroups.Static;
        if (hello.Key.Length != eph.ElementLength)
            throw new CryptographicException($"client ephemeral key is {hello.Key.Length} bytes, expected {eph.ElementLength}");
        BigInteger e = eph.RandomExponent();
        byte[] pub = Big.ToFixed(eph.PublicFor(e), eph.ElementLength);
        byte[] zStatic = stat.Agree(keys.ServerStaticPrivate, keys.ClientStaticPublic);
        byte[] zEph = eph.Agree(e, Big.FromBigEndian(hello.Key));
        var ciphers = new ConnectionCiphers(LoginKdf.KeyMaterial(zStatic, zEph), isClient: false);
        return (new KeyExchange(pub, keys.Signing.Sign(pub)).Encode(), ciphers);
    }
}

/// <summary>
/// Client side, reproducing what the official client does (for tests and tooling): Crypto++ DH2 with the embedded
/// static private key and server static public value, signature check with the embedded DSA key, SHA-512 KDF.
/// </summary>
public sealed class ClientHandshake(BigInteger clientStaticPrivate, BigInteger serverStaticPublic, DsaKey verifyKey)
{
    private readonly BigInteger _eph = LoginGroups.Ephemeral.RandomExponent();

    public byte[] Hello() =>
        new KeyExchange(Big.ToFixed(LoginGroups.Ephemeral.PublicFor(_eph), LoginGroups.Ephemeral.ElementLength), []).Encode();

    public ConnectionCiphers Complete(KeyExchange reply)
    {
        // "General Rhoa Failure #3" in the client when this fails.
        if (!verifyKey.Verify(reply.Key, reply.Signature)) throw new CryptographicException("server key signature invalid");
        byte[] zStatic = LoginGroups.Static.Agree(clientStaticPrivate, serverStaticPublic);
        byte[] zEph = LoginGroups.Ephemeral.Agree(_eph, Big.FromBigEndian(reply.Key));
        return new ConnectionCiphers(LoginKdf.KeyMaterial(zStatic, zEph), isClient: true);
    }
}

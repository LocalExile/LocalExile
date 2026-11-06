using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Everything the login server needs to complete the client's handshake. Generated once from a client image
/// (<see cref="Generate"/>) and stored as JSON; the same keys are then patched into the client (<see cref="ClientImage.Patch"/>).
/// </summary>
public sealed record LoginServerKeys(DsaKey Signing, BigInteger ServerStaticPrivate, BigInteger ClientStaticPublic)
{
    public BigInteger ServerStaticPublic => LoginGroups.Static.PublicFor(ServerStaticPrivate);

    /// <summary>New signing key over the client's own DSA domain (so p, q, g and the DER length stay the same) and a new
    /// server static key; the client's embedded static private key gives us its static public value.</summary>
    public static LoginServerKeys Generate(ClientImage client)
    {
        DsaKey original = client.ReadVerifyKey();
        DsaKey signing;
        do
        {
            BigInteger x = Big.RandomBelow(original.Q - 1) + 1;
            signing = original with { X = x, Y = BigInteger.ModPow(original.G, x, original.P) };
        } while (ClientImage.EncodeVerifyKey(signing).Length != ClientImage.VerifyKeyLength);
        return new LoginServerKeys(signing, LoginGroups.Static.RandomExponent(),
            LoginGroups.Static.PublicFor(client.ReadClientStaticPrivate()));
    }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["dsaP"] = H(Signing.P), ["dsaQ"] = H(Signing.Q), ["dsaG"] = H(Signing.G), ["dsaY"] = H(Signing.Y),
        ["dsaX"] = H(Signing.X ?? throw new InvalidOperationException("signing key has no private part")),
        ["serverStaticPrivate"] = H(ServerStaticPrivate), ["clientStaticPublic"] = H(ClientStaticPublic),
    }, new JsonSerializerOptions { WriteIndented = true });

    public static LoginServerKeys FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new FormatException("empty key file");
        BigInteger F(string k) => d.TryGetValue(k, out var v)
            ? BigInteger.Parse("0" + v, NumberStyles.HexNumber) : throw new FormatException($"key file missing '{k}'");
        return new LoginServerKeys(new DsaKey(F("dsaP"), F("dsaQ"), F("dsaG"), F("dsaY"), F("dsaX")),
            F("serverStaticPrivate"), F("clientStaticPublic"));
    }

    private static string H(BigInteger v) => v.ToString("x");
}

/// <summary>
/// The login key blobs inside the client executable. They sit together in .rdata and are only referenced from
/// the client's login code (3.29.3b reference VAs 0x142fc0150 / 0x142fc04a0 / 0x142fc0620; 3.29.3c shifts
/// them). Located by the DER header, not by fixed offsets.
/// </summary>
public sealed class ClientImage(byte[] bytes)
{
    public const int VerifyKeyLength = 0x346;        // X.509 SubjectPublicKeyInfo, DSA p=2048 q=224
    private const int ClientStaticPrivateOffset = 0x350; // 384 bytes, exponent in the 3072-bit group
    private const int ServerStaticPublicOffset = 0x4d0;  // 384 bytes, element of the 3072-bit group
    private const int StaticLength = 384;

    // SEQUENCE(0x342) SEQUENCE(0x235) OID 1.2.840.10040.4.1 (dsa)
    private static readonly byte[] DerPrefix = Convert.FromHexString("308203423082023506072a8648ce380401");

    public byte[] Bytes { get; } = bytes;

    public int Offset { get; } = FindUnique(bytes);

    public DsaKey ReadVerifyKey()
    {
        var spki = new AsnReader(Bytes.AsMemory(Offset, VerifyKeyLength), AsnEncodingRules.DER).ReadSequence();
        var alg = spki.ReadSequence();
        alg.ReadObjectIdentifier();
        var prm = alg.ReadSequence();
        BigInteger p = prm.ReadInteger(), q = prm.ReadInteger(), g = prm.ReadInteger();
        byte[] bits = spki.ReadBitString(out _);
        BigInteger y = new AsnReader(bits, AsnEncodingRules.DER).ReadInteger();
        return new DsaKey(p, q, g, y);
    }

    public BigInteger ReadClientStaticPrivate() =>
        Big.FromBigEndian(Bytes.AsSpan(Offset + ClientStaticPrivateOffset, StaticLength));

    public BigInteger ReadServerStaticPublic() =>
        Big.FromBigEndian(Bytes.AsSpan(Offset + ServerStaticPublicOffset, StaticLength));

    /// <summary>Overwrites the verify key and the server static public value in place (same sizes).</summary>
    public void Patch(LoginServerKeys keys)
    {
        byte[] der = EncodeVerifyKey(keys.Signing);
        if (der.Length != VerifyKeyLength) throw new InvalidOperationException("verify key would change size");
        der.CopyTo(Bytes, Offset);
        Big.ToFixed(keys.ServerStaticPublic, StaticLength).CopyTo(Bytes, Offset + ServerStaticPublicOffset);
    }

    public static byte[] EncodeVerifyKey(DsaKey key)
    {
        var inner = new AsnWriter(AsnEncodingRules.DER);
        inner.WriteInteger(key.Y);
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier("1.2.840.10040.4.1");
                using (w.PushSequence()) { w.WriteInteger(key.P); w.WriteInteger(key.Q); w.WriteInteger(key.G); }
            }
            w.WriteBitString(inner.Encode());
        }
        return w.Encode();
    }

    private static int FindUnique(byte[] bytes)
    {
        int at = bytes.AsSpan().IndexOf(DerPrefix);
        if (at < 0) throw new FormatException("login verify key not found in client image");
        if (bytes.AsSpan(at + 1).IndexOf(DerPrefix) >= 0) throw new FormatException("login verify key header is not unique");
        if (at + ServerStaticPublicOffset + StaticLength > bytes.Length) throw new FormatException("truncated client image");
        return at;
    }
}

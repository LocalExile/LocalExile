namespace GameServer.Data.Terrain.Generation;

/// <summary>The client's small generator (TinyMT32's parameters): <c>0x140e0a760</c> seeds it (TinyMT32's
/// initialisation, then eight steps) and <c>0x140485560</c> draws a uniform index (32-bit outputs, the last
/// incomplete bucket rejected). Its step keeps the client's order, which differs from TinyMT32's in which word
/// gets which shift.</summary>
struct TinyMt
{
    const uint Mat1 = 0x8f7011ee, Mat2 = 0xfc78ff1f, TMat = 0x3793fdff;
    uint _s0, _s1, _s2, _s3;

    public TinyMt(uint seed)
    {
        uint[] s = [seed, Mat1, Mat2, TMat];
        for (uint i = 1; i < 8; i++)
            s[i & 3] ^= i + 0x6c078965u * (s[(i - 1) & 3] ^ (s[(i - 1) & 3] >> 30));
        (_s0, _s1, _s2, _s3) = (s[0], s[1], s[2], s[3]);
        for (int i = 0; i < 8; i++) Next();
    }

    uint Next()
    {
        uint x = (_s0 & 0x7fffffff) ^ _s1 ^ _s2;
        uint y = _s3 ^ (_s3 << 1);
        x = (x >> 1) ^ x ^ y;
        uint s1 = _s1;
        _s0 = s1;
        _s1 = (0u - (x & 1) & Mat1) ^ _s2;
        _s2 = (0u - (x & 1) & Mat2) ^ (x << 10) ^ y;
        _s3 = x;
        uint t = (_s2 >> 8) + s1;
        return (0u - (t & 1) & TMat) ^ t ^ x;
    }

    public uint Pick(uint n)
    {
        if (n <= 1) return 0;
        uint v;
        do v = Next();
        while (uint.MaxValue / n <= v / n && uint.MaxValue % n != n - 1);
        return v % n;
    }
}

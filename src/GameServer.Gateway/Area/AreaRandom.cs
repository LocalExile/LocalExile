/// <summary>
/// The area's randomness: a small generator (SplitMix64) seeded from the area's seed, an entity, the frame and a stream
/// number, so what an entity rolls in a frame does not depend on what others rolled before it or on which thread asked. The
/// same seed and inputs give the same rolls (replays).
/// </summary>
public struct AreaRandom(ulong seed)
{
    ulong state = seed;

    public static AreaRandom For(ulong areaSeed, EntityId id, long frame, int stream = 0) =>
        new(Mix(areaSeed ^ Mix(id.Value * 0x9E3779B97F4A7C15UL) ^ Mix((ulong)frame * 0xBF58476D1CE4E5B9UL) ^ Mix((ulong)stream + 1)));

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextU64() => Mix(state += 0x9E3779B97F4A7C15UL);
    /// <summary>0 to <paramref name="max"/> - 1.</summary>
    public int Next(int max) => max <= 0 ? 0 : (int)(NextU64() % (ulong)max);
    /// <summary><paramref name="min"/> to <paramref name="max"/> - 1.</summary>
    public int Next(int min, int max) => max <= min ? min : min + Next(max - min);
    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));
    /// <summary>True with chance <paramref name="p"/> (0 to 1).</summary>
    public bool Chance(double p) => NextDouble() < p;
}

/// <summary>
/// The area's <see cref="Random"/> for rules that take one, seeded from the area's seed so a replay of the same inputs
/// rolls the same. Locked: an area's packet handlers and its frame can both roll.
/// </summary>
public sealed class AreaSeededRandom(ulong seed) : Random((int)(seed ^ (seed >> 32)))
{
    readonly Lock gate = new();
    public override int Next() { lock (gate) return base.Next(); }
    public override int Next(int maxValue) { lock (gate) return base.Next(maxValue); }
    public override int Next(int minValue, int maxValue) { lock (gate) return base.Next(minValue, maxValue); }
    public override long NextInt64() { lock (gate) return base.NextInt64(); }
    public override long NextInt64(long maxValue) { lock (gate) return base.NextInt64(maxValue); }
    public override long NextInt64(long minValue, long maxValue) { lock (gate) return base.NextInt64(minValue, maxValue); }
    public override double NextDouble() { lock (gate) return base.NextDouble(); }
    public override float NextSingle() { lock (gate) return base.NextSingle(); }
    public override void NextBytes(byte[] buffer) { lock (gate) base.NextBytes(buffer); }
    public override void NextBytes(Span<byte> buffer) { lock (gate) base.NextBytes(buffer); }
    protected override double Sample() { lock (gate) return base.Sample(); }
}

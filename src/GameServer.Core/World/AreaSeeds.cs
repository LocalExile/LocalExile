namespace GameServer.Core.World;

/// <summary>
/// The seed of a new non-town instance (Auth's <see cref="InstanceRegistry.SeedFor"/>), by WorldAreas <c>HASH16</c>.
/// <list type="bullet">
/// <item>A hideout: always 1 (its layout is fixed).</item>
/// <item><see cref="Fixed"/> (dev, Game_SEED): that seed for every other area, even one the client cannot build.</item>
/// <item>An area of <see cref="Random"/> (the ones the Gateway generates itself): a random seed (1 … int.MaxValue).</item>
/// <item>Any other area: seed 1, the seed its captures were dumped at, unless the client cannot build seed 1 there
/// (<see cref="InPlaceOfOne"/>, <see cref="ServerLevelAreas.SeedsInPlaceOfOne"/>): then the seed it is played at
/// instead, so pinned seeds (Game_RANDOM_SEEDS=0) never hand the client a map it fails to generate.</item>
/// </list>
/// </summary>
public sealed class AreaSeeds
{
    public IReadOnlySet<ushort> Hideouts { get; init; } = new HashSet<ushort>();
    public IReadOnlySet<ushort> Random { get; init; } = new HashSet<ushort>();
    public IReadOnlyDictionary<ushort, uint> InPlaceOfOne { get; init; } = new Dictionary<ushort, uint>();
    public uint? Fixed { get; init; }

    /// <summary>Where random seeds come from (tests replace it).</summary>
    public Func<uint> Next { get; init; } = () => (uint)System.Random.Shared.Next(1, int.MaxValue);

    public uint For(ushort area)
    {
        if (Hideouts.Contains(area)) return 1;
        if (Fixed is { } seed) return seed;
        if (Random.Contains(area)) return Next();
        return InPlaceOfOne.TryGetValue(area, out uint other) ? other : 1;
    }
}

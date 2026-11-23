namespace GameServer.Core.World;

/// <summary>
/// The seed of a new non-town instance (Auth's <see cref="InstanceRegistry.SeedFor"/>), by WorldAreas <c>HASH16</c>.
/// <list type="bullet">
/// <item>A hideout: always 1 (its layout is fixed).</item>
/// <item><see cref="Fixed"/> (dev, Game_SEED): that seed for every other area, even one the client cannot build.</item>
/// <item>An area of <see cref="Random"/> (the ones the Gateway generates itself): a random seed (1 … int.MaxValue).</item>
/// <item>Another area the client cannot build at seed 1 (<see cref="InPlaceOfOne"/>,
/// <see cref="ServerLevelAreas.SeedsInPlaceOfOne"/>): the seed it is played at instead, so pinned seeds
/// (Game_RANDOM_SEEDS=0) never hand the client a map it fails to generate.</item>
/// <item>A map area (<see cref="Maps"/>, <see cref="WorldAreaMaps"/>) with neither: a random seed. Seed 1 is the seed an
/// area's captures were dumped at, and a map nobody captured has no layout pinned there — the client generates its own,
/// as in the real game. Pinning it can hand the client a layout it cannot build and the client exits
/// (<c>MapWorldsColosseum</c> at seed 1: "no suitable room found for room at (4, 3) with type cage_turn").</item>
/// <item>Any other area: seed 1, the seed its captures were dumped at.</item>
/// </list>
/// </summary>
public sealed class AreaSeeds
{
    public IReadOnlySet<ushort> Hideouts { get; init; } = new HashSet<ushort>();
    public IReadOnlySet<ushort> Random { get; init; } = new HashSet<ushort>();
    public IReadOnlyDictionary<ushort, uint> InPlaceOfOne { get; init; } = new Dictionary<ushort, uint>();
    /// <summary>The areas a map item opens (<see cref="WorldAreaMaps"/>): the ones with no capture at seed 1 that get a
    /// random layout instead of being pinned to a seed the client may fail to build.</summary>
    public IReadOnlySet<ushort> Maps { get; init; } = new HashSet<ushort>();
    public uint? Fixed { get; init; }

    /// <summary>Where random seeds come from (tests replace it).</summary>
    public Func<uint> Next { get; init; } = () => (uint)System.Random.Shared.Next(1, int.MaxValue);

    public uint For(ushort area)
    {
        if (Hideouts.Contains(area)) return 1;
        if (Fixed is { } seed) return seed;
        if (Random.Contains(area)) return Next();
        if (InPlaceOfOne.TryGetValue(area, out uint other)) return other;
        return Maps.Contains(area) ? Next() : 1;
    }
}

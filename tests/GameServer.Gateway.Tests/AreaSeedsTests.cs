using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>A new instance's seed (AreaSeeds): a map nobody captured must not be pinned to seed 1, which is the seed
/// captures are dumped at and one the client can fail to build (MapWorldsColosseum exits on it).</summary>
public sealed class AreaSeedsTests
{
    static AreaSeeds Seeds(ushort map) => new()
    {
        Hideouts = new HashSet<ushort> { 0x1001 },
        Random = new HashSet<ushort> { 0x1002 },
        InPlaceOfOne = new Dictionary<ushort, uint> { [0x1003] = 460946050 },
        Maps = new HashSet<ushort> { map },
        Next = () => 1234567,
    };

    [Fact]
    public void A_map_with_no_capture_gets_a_random_seed_and_every_other_area_keeps_its_own()
    {
        const ushort map = 0x2725, other = 0x3001;
        var seeds = Seeds(map);

        // A map area with neither a capture nor a verified seed: random, not 1 (the seed the client may not build).
        Assert.Equal(1234567u, seeds.For(map));
        // A hideout has a fixed layout; an area the Gateway generates is random; a verified area keeps the seed it was
        // verified at.
        Assert.Equal(1u, seeds.For(0x1001));
        Assert.Equal(1234567u, seeds.For(0x1002));
        Assert.Equal(460946050u, seeds.For(0x1003));
        // An area that is no map and has nothing verified keeps seed 1, its captures' seed.
        Assert.Equal(1u, seeds.For(other));
        // The dev seed overrides everything but a hideout.
        Assert.Equal(99u, new AreaSeeds
        {
            Hideouts = new HashSet<ushort> { 0x1001 }, Maps = new HashSet<ushort> { map }, Fixed = 99, Next = () => 1234567,
        }.For(map));
        Assert.Equal(1u, new AreaSeeds { Hideouts = new HashSet<ushort> { map }, Fixed = 99 }.For(map));
    }
}

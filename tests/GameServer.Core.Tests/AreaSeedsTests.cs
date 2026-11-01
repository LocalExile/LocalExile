using GameServer.Core.World;

namespace GameServer.Core.Tests;

// The seed of a new instance (Auth's InstanceRegistry.SeedFor): hideouts 1, Game_SEED for the rest, random for the
// areas the Gateway generates, else seed 1 or the seed the client can build in its place.
public class AreaSeedsTests
{
    const ushort Hideout = 0x1111, Random = 0x2222, SeedOneFails = 0x3333, Captured = 0x4444;

    static AreaSeeds Seeds(uint? fixedSeed = null, bool random = true) => new()
    {
        Hideouts = new HashSet<ushort> { Hideout },
        Random = random ? new HashSet<ushort> { Random, SeedOneFails } : new HashSet<ushort>(),
        InPlaceOfOne = new Dictionary<ushort, uint> { [SeedOneFails] = 562400817 },
        Fixed = fixedSeed,
        Next = () => 777,
    };

    [Fact] public void HideoutsAlwaysUseSeedOne()
    {
        Assert.Equal(1u, Seeds().For(Hideout));
        Assert.Equal(1u, Seeds(fixedSeed: 42).For(Hideout));
    }

    [Fact] public void ServerGeneratedAreasGetRandomSeeds()
    {
        Assert.Equal(777u, Seeds().For(Random));
        Assert.Equal(777u, Seeds().For(SeedOneFails));
    }

    // Game_RANDOM_SEEDS=0: seed 1, except where the client cannot build it.
    [Fact] public void PinnedSeedsAvoidSeedOneWhereTheClientCannotBuildIt()
    {
        Assert.Equal(1u, Seeds(random: false).For(Random));
        Assert.Equal(1u, Seeds(random: false).For(Captured));
        Assert.Equal(562400817u, Seeds(random: false).For(SeedOneFails));
    }

    [Fact] public void AFixedSeedIsKeptEverywhereButHideouts() => Assert.Equal(42u, Seeds(fixedSeed: 42).For(SeedOneFails));

    [Fact] public void TheRegistryUsesTheRuleForNewInstances()
    {
        var registry = new InstanceRegistry { SeedFor = Seeds(random: false).For, Towns = new HashSet<ushort>() };
        Assert.Equal(562400817u, registry.Enter(SeedOneFails, "a").Seed);
    }
}

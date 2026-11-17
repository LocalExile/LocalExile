namespace GameServer.Gateway.Tests;

/// <summary>The Atlas passive tree's pathing rule on a small graph: start 1 - 2 - 3 - 4, with 6 off node 2 and 9 alone.</summary>
public sealed class AtlasPassiveConnectivityTests
{
    static readonly Dictionary<ushort, HashSet<ushort>> Links = new()
    {
        [1] = [2], [2] = [1, 3, 6], [3] = [2, 4], [4] = [3], [6] = [2], [9] = [],
    };

    [Fact]
    public void AllocationNeedsAPathAndRefundMayNotCutTheTree()
    {
        Assert.True(AtlasPassives.CanAllocate([], 2, Links, 1));
        Assert.False(AtlasPassives.CanAllocate([], 3, Links, 1));
        Assert.False(AtlasPassives.CanAllocate([2], 9, Links, 1));
        Assert.False(AtlasPassives.CanAllocate([2], 1, Links, 1));
        Assert.True(AtlasPassives.CanAllocate([2], 3, Links, 1));
        Assert.False(AtlasPassives.CanAllocate([3], 4, Links, 1));
        Assert.False(AtlasPassives.CanRefund([2, 3, 4], 3, Links, 1));
        Assert.False(AtlasPassives.CanRefund([2, 3, 4], 2, Links, 1));
        Assert.True(AtlasPassives.CanRefund([2, 3, 4], 4, Links, 1));
        Assert.True(AtlasPassives.CanRefund([2, 3, 4, 9], 4, Links, 1));
    }
}

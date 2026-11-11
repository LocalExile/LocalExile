namespace GameServer.Gateway.Tests;

public sealed class DarkBargainTests
{
    [Fact]
    public void ChainStartsNearTheAimThenJumpsToTheNearestUnusedSkeletonAndNeverPaysTheLastPoint()
    {
        (uint, (int, int), uint, uint)[] skeletons =
        [
            (1, (100, 100), 1000, 1000), (2, (120, 100), 50, 1000), (3, (150, 100), 1000, 1000),
            (4, (100 + Game.DarkBargainChainRadius * 4, 100), 1000, 1000),
        ];
        var blasts = Game.DarkBargainChain(skeletons, aim: (105, 100), player: (0, 0), percent: 12, chains: 2);
        Assert.Equal([1u, 2u, 3u], blasts.Select(b => b.Skeleton));
        // 12% of 1000 is 120; the hurt skeleton keeps its last point (49 of its 50).
        Assert.Equal([120, 49, 120], blasts.Select(b => b.Sacrificed));
        Assert.Equal([1u, 2u], Game.DarkBargainChain(skeletons, (105, 100), (0, 0), 12, chains: 1).Select(b => b.Skeleton));
        // No skeleton near the aim: the one nearest the player starts it. None near either: the caster pays instead.
        Assert.Equal(3u, Game.DarkBargainChain(skeletons, (5000, 5000), (155, 100), 12, 0).Single().Skeleton);
        Assert.Empty(Game.DarkBargainChain(skeletons, (5000, 5000), (5000, 5000), 12, 2));
        Assert.Empty(Game.DarkBargainChain([], (105, 100), (100, 100), 12, 2));
    }

    [Fact]
    public void CasterPaysAShareOfMaximumLifeButNeverItsLastPointAndTheBlastUsesItsOwnRadius()
    {
        Assert.Equal(360, Game.DarkBargainLifePaid(3000, 3000, 12));
        Assert.Equal(99, Game.DarkBargainLifePaid(100, 3000, 12));
        Assert.Equal(0, Game.DarkBargainLifePaid(1, 3000, 12));
        Assert.Equal(0, Game.DarkBargainLifePaid(0, 3000, 12));
        Assert.Equal(26, SkillArea.BaseRadius("DarkPact", _ => 0));
        Assert.Equal(26, SkillArea.BaseRadius("DarkPactRoyale", _ => 0));
    }
}

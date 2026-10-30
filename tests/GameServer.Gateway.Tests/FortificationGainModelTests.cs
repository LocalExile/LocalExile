using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FortificationGainModelTests
{
    [Theory]
    [InlineData(MonsterRarity.Normal, 2)]
    public void Version_one_rarity_factors_are_explicit_LocalExile_policy(MonsterRarity rarity, int expected)
    {
        Assert.Equal(1, FortificationGainModel.Version);
        Assert.Equal(expected, FortificationGainModel.Calculate(1, 100, rarity, 30));
    }

}

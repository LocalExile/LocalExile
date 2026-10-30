using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class MonsterLifeDataTests
{
    [Theory]
    [InlineData(true, false, false, 0u, true)]
    public void Ordinary_source_requires_known_usable_no_veto_and_zero_life(
        bool? usable, bool? sinking, bool? veto, uint life, bool expected) =>
        Assert.Equal(expected, new MonsterLifeData(usable, sinking, veto).IsUsableAsCorpse(life));

}

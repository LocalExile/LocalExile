using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class AreaInstanceStatsTests
{
    [Fact]
    public void Phrecia_family_uses_the_world_stat_list_without_changing_the_league_or_other_fields()
    {
        byte[] ordinary = new AreaInstance(1, 0x235c, 1, 0).Encode();
        byte[] phrecia = new AreaInstance(1, 0x235c, 1, 0, LeagueStats: new Dictionary<uint, int>
        {
            [21146] = 1 // league_enable_alt_ascendancies in the verified target client's Stats.dat
        }).Encode();
        Assert.Equal(ordinary.Length + 7, phrecia.Length);
        byte[] prefix = phrecia[..ordinary.Length];
        Assert.Equal((byte)0x40, prefix[18]);
        prefix[18] = 0;
        Assert.Equal(ordinary, prefix);
        // Compact unsigned count 1; unsigned key 21146 (0xc0 0x52 0x9a); signed value 1; second/final lists empty.
        Assert.Equal(new byte[] { 1, 0xc0, 0x52, 0x9a, 1, 0, 0 }, phrecia[ordinary.Length..]);
        Assert.Equal(ordinary, new AreaInstance(1, 0x235c, 1, 0, LeagueStats: new Dictionary<uint, int>()).Encode());
    }
}

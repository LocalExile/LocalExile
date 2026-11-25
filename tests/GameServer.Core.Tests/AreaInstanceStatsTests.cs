using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class AreaInstanceStatsTests
{
    [Fact]
    public void Mirror_map_registers_its_ruleset_before_stats_and_terrain()
    {
        var terrain = InstanceProtocol.TerrainPluginStates([(2161, [0])]);
        var map = new AreaInstance(1, 0x235c, 1, 68, AchievementSets: [2], TerrainData: terrain,
            LeagueStats: new Dictionary<uint, int> { [3] = 1 });
        var ordinary = map.Encode();
        Assert.Equal(ordinary, (map with { Rulesets = [] }).Encode());
        var mirror = (map with { Rulesets = [102], PreloadGroups = [52] }).Encode();
        Assert.Equal(5, mirror[19]);
        Assert.Equal(new byte[] { 0, 1, 53, 1, 2, 0, 0, 1, 102 }, mirror[26..35]);
        Assert.Equal(ordinary[32..], mirror[35..]);
        Assert.Throws<ArgumentOutOfRangeException>(() => (map with { Rulesets = new byte[256] }).Encode());
    }

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

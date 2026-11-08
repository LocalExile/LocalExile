using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BladefallTrarthusTests
{
    // #584: the client times Bladefall of Trarthus' volleys by the virtual bladefall_volley_frequency_ms in the skill record;
    // 0 hung it on the first use. The original server's figures at 691 maximum mana: a volley every ~625 ms and
    // mana_spent_per_minute 2073, and each volley's ground blade in the blob below (its base-data entry left out).
    [Fact]
    public void Volley_interval_mana_spent_and_ground_blade_match_the_original_server()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow(BladefallVolleys.EffectId);
        Assert.True(effect >= 0);
        var own = skills.EffectStats(effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var stats = new StatCatalog();
        int Own(string name) => stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        Assert.Equal(700, Own("bladefall_base_volley_frequency_ms"));
        Assert.Equal(300, Own("base_mana_cost_%_per_minute"));

        Assert.Equal(625, BladefallVolleys.FrequencyMs(Own("bladefall_base_volley_frequency_ms"),
            Own("bladefall_volley_frequency_+%_per_100_maximum_mana"), 691));
        Assert.Equal(700, BladefallVolleys.FrequencyMs(700, 2, 99));
        Assert.Equal(2073, BladefallVolleys.ManaPerMinute(Own("base_mana_cost_%_per_minute"), 691));
        Assert.NotNull(BladefallVolleys.BuffRow(BladefallVolleys.CostBuff));
        Assert.NotNull(BladefallVolleys.BuffRow(BladefallVolleys.BladesBuff));

        var blade = new GroundBladeObject(386, 321, BitConverter.UInt32BitsToSingle(0x3f5cb72e), 7781).Blob();
        Assert.Equal(Convert.FromHexString(
            "0200" + "82010000" + "41010000" + "2EB75C3F" + "0100" + "0000"
            + "0B" + "651E0000" + "02000000" + "00000000" + "0000803F" + "01" + "01000000" + "00"), blade);
    }
}

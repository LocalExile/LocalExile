namespace GameServer.Gateway.Tests;

public sealed class FireMasteryRegenerationTests
{
    // Fire Mastery: "Regenerate 1 Life per second for each 1% uncapped Fire Resistance" (60 per minute per 1%).
    [Fact]
    public void Fire_mastery_regenerates_life_per_uncapped_fire_resistance()
    {
        static Func<string, int> Get(int resistance, int increased = 0) => name => name switch
        {
            "life_regeneration_per_minute_per_1%_uncapped_fire_damage_resistance" => 60,
            "uncapped_fire_damage_resistance_%" => resistance,
            "life_regeneration_rate_+%" => increased,
            _ => 0,
        };

        // 90% uncapped (75% capped) regenerates 90 life per second; increased regeneration scales it.
        Assert.Equal(90 * 60, PanelStats.LifePerMinute(Get(90), 500), 6);
        Assert.Equal(90 * 60 * 1.5, PanelStats.LifePerMinute(Get(90, increased: 50), 500), 6);
        // Negative fire resistance gives no regeneration rather than degeneration.
        Assert.Equal(0, PanelStats.LifePerMinute(Get(-30), 500), 6);
    }
}

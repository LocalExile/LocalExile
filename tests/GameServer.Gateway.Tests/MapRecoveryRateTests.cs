namespace GameServer.Gateway.Tests;

public sealed class MapRecoveryRateTests
{
    [Fact]
    public void Map_recovery_scales_life_and_shield_multiplicatively_and_resets_when_leaving()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        void Stat(string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        Stat("base_life_regeneration_rate_per_minute", 1000);
        Stat("life_recovery_rate_+%", 50);
        Stat("base_energy_shield_regeneration_rate_per_minute", 600);
        Stat("energy_shield_recovery_rate_+%", 50);
        (double Life, double Mana, double Shield, double Recharge) Rates()
        {
            var stats = Game.CharacterPlayerStats(conn);
            int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            return (PanelStats.LifePerMinute(Get, conn.Pools.Life), PanelStats.ManaPerMinute(Get, conn.Pools.Mana),
                EnergyShield.RegenPerMinute(300, Get), EnergyShield.RechargePerMinute(300, Get));
        }

        var ordinary = Rates();
        Assert.Equal((1500d, 900d, 9000d), (ordinary.Life, ordinary.Shield, ordinary.Recharge));
        var area = new MapDevice.MapArea(conn.Layout.Hash, "MapWorldsStrand", "Test map", 16);
        var mods = new MapMods(new Dictionary<string, int> { [PanelStats.MapLifeAndEnergyShieldRecovery] = -60 });
        conn.Instance!.OpenedWith(new MapDevice.Run(conn.Character!, area, mods));
        Assert.Equal((600d, ordinary.Mana, 360d, 3600d), Rates());
        conn.Instance = new();
        Assert.Equal(ordinary, Rates());
    }
}

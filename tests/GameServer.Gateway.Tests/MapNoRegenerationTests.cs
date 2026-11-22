namespace GameServer.Gateway.Tests;

public sealed class MapNoRegenerationTests
{
    [Fact]
    public void Map_stops_all_three_regeneration_rates_and_allows_energy_shield_recharge()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        conn.PlayerStats[Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value] = 600;
        conn.PlayerStats[Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value] = 600;
        (double Life, double Mana, double Shield, double Recharge) Rates(ClientState player)
        {
            var stats = Game.CharacterPlayerStats(player);
            int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            return (PanelStats.LifePerMinute(Get, player.Pools.Life), PanelStats.ManaPerMinute(Get, player.Pools.Mana),
                EnergyShield.RegenPerMinute(300, Get), EnergyShield.RechargePerMinute(300, Get));
        }

        var ordinary = Rates(conn);
        Assert.Equal(600, ordinary.Life);
        Assert.True(ordinary.Mana > 0 && ordinary.Shield > 0 && ordinary.Recharge > 0);
        var area = new MapDevice.MapArea(conn.Layout.Hash, "MapWorldsStrand", "Test map", 16);
        var mods = new MapMods(new Dictionary<string, int> { [PanelStats.MapNoRegeneration] = 1 });
        conn.Instance!.OpenedWith(new MapDevice.Run(conn.Character!, area, mods));
        Assert.Equal((0d, 0d, 0d, ordinary.Recharge), Rates(conn));
        var returned = new ClientState(conn.Layout, 2, 1)
            { Instance = new(), Character = conn.Character, Pools = conn.Pools, LeagueModule = conn.LeagueModule };
        foreach (var row in conn.PlayerStats) returned.PlayerStats[row.Key] = row.Value;
        Assert.Equal(ordinary, Rates(returned));
    }
}

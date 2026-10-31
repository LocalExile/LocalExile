namespace GameServer.Gateway.Tests;

public class GhostReaverLeechTests
{
    static LeechSession Session(Dictionary<string, int> raw, uint life = 1000, uint shield = 1000)
    {
        int Get(string n) => raw.GetValueOrDefault(n);
        var s = new LeechSession();
        s.Configure(0, LeechLimits.Resolve(Get, life, 1000, shield), Get, _ => null);
        return s;
    }

    [Fact]
    public void Keystone_doubles_the_total_ES_cap_without_doubling_the_instance_cap()
    {
        int Get(string n) => n == "keystone_ghost_reaver" ? 1 : 0;
        var es = LeechLimits.Resolve(Get, 1000, 1000, 1000).Resources[2];
        Assert.Equal(100, es.MaximumPerInstance);
        Assert.Equal(200, es.MaximumPerSecond);
        Assert.Equal(0, EnergyShield.RechargePerMinute(1000, Get));
        Assert.Equal(1, PanelStats.Compute(Get).ToDictionary(x => x.Stat, x => x.Value)
            .GetValueOrDefault("virtual_leech_energy_shield_instead_of_life"));
    }

    [Fact]
    public void Converted_life_and_native_ES_create_two_independent_ES_instances()
    {
        var s = Session(new() { ["keystone_ghost_reaver"] = 1 });
        s.Hit(0, (100, 0, 40), false, false, false);
        Assert.Equal(0, s.Life.Count);
        Assert.Equal(2, s.EnergyShield.Count);
        Assert.Equal((0d, 0d, 40d), s.TakeRecovery(1));
        Assert.Equal((0d, 0d, 100d), s.TakeRecovery(10)); // 2s for 40, 5s for 100; not one combined 7s instance.
    }

    [Fact]
    public void Converted_instances_use_ES_pool_caps_speed_and_recovery()
    {
        var s = Session(new() { ["keystone_ghost_reaver"] = 1,
            ["life_leech_speed_+%"] = 500,
            ["energy_shield_leech_speed_+%"] = 100, ["energy_shield_recovery_rate_+%"] = 50 }, life: 1);
        Assert.Equal(default, s.Hit(0, (500, 0, 0), true, false, false));
        Assert.Equal((0d, 0d, 300d), s.TakeRecovery(10)); // ES cap 100, five seconds at 20 * 2 * 1.5.
        Assert.Equal(0, s.Life.Count);
    }

    [Fact]
    public void Plain_conversion_does_not_grant_the_keystones_cap_or_recharge_effects()
    {
        Dictionary<string, int> raw = new() { ["leech_energy_shield_instead_of_life"] = 1,
            ["leech_%_is_instant"] = 100 };
        var s = Session(raw);
        Assert.Equal((0d, 0d, 150d), s.Hit(0, (100, 0, 50), true, false, false));
        int Get(string n) => raw.GetValueOrDefault(n);
        Assert.Equal(100, LeechLimits.Resolve(Get, 1000, 1000, 1000).Resources[2].MaximumPerSecond);
        Assert.Equal(20000, EnergyShield.RechargePerMinute(1000, Get));
    }

    [Fact]
    public void Removing_conversion_does_not_reclassify_existing_instances()
    {
        Dictionary<string, int> raw = new() { ["keystone_ghost_reaver"] = 1 };
        var s = Session(raw);
        s.Hit(0, (100, 0, 0), false, false, false);
        raw.Clear(); int Get(string n) => raw.GetValueOrDefault(n);
        s.Configure(1, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
        s.Hit(1, (100, 0, 0), false, false, false);
        Assert.Equal(1, s.Life.Count);
        Assert.Equal(1, s.EnergyShield.Count);
        Assert.Equal((100d, 0d, 100d), s.TakeRecovery(10));
    }

    [Theory]
    [InlineData("base_cannot_leech", 0)]
    [InlineData("base_cannot_leech_energy_shield", 0)]
    [InlineData("base_cannot_leech_life", 1)]
    public void Source_and_destination_prohibitions_gate_converted_grants(string prohibition, int count)
    {
        var s = Session(new() { ["keystone_ghost_reaver"] = 1, [prohibition] = 1 });
        s.Hit(0, (100, 0, 100), false, false, false);
        Assert.Equal(0, s.Life.Count);
        Assert.Equal(count, s.EnergyShield.Count);
    }

    [Fact]
    public void Explicit_zero_conversion_replacement_does_not_remove_other_keystone_effects()
    {
        int Get(string n) => n == "keystone_ghost_reaver" ? 1 : 0;
        var s = new LeechSession();
        var limits = LeechLimits.Resolve(Get, 1000, 1000, 1000);
        s.Configure(0, limits, Get, n => n == "virtual_leech_energy_shield_instead_of_life" ? 0 : null);
        s.Hit(0, (100, 0, 0), false, false, false);
        Assert.Equal(1, s.Life.Count);
        Assert.Equal(0, s.EnergyShield.Count);
        Assert.Equal(200, limits.Resources[2].MaximumPerSecond);
    }

    [Fact]
    public void Independent_converted_and_native_instances_share_one_total_ES_cap()
    {
        var s = Session(new() { ["keystone_ghost_reaver"] = 1, ["energy_shield_recovery_rate_+%"] = 50 });
        for (int i = 0; i < 6; i++) s.Hit(0, (100, 0, 100), false, false, false);
        Assert.Equal(12, s.EnergyShield.Count);
        Assert.Equal((0d, 0d, 300d), s.TakeRecovery(1));
    }
}

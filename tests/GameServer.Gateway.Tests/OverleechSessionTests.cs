namespace GameServer.Gateway.Tests;

public class OverleechSessionTests
{
    const string Attacks = "energy_shield_leech_from_attacks_does_not_stop_on_full_energy_shield";
    static void Configure(LeechSession state, double at, Dictionary<string, int> raw)
    {
        int Get(string n) => raw.GetValueOrDefault(n);
        state.Configure(at, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get, _ => null);
    }

    [Fact]
    public void Full_pool_removes_only_nonattack_instances_without_extending_retained_expiry()
    {
        var s = new LeechSession();
        Configure(s, 0, new() { [Attacks] = 1 });
        s.Hit(0, (0, 0, 100), false, false, false, attack: true);
        s.Hit(0, (0, 0, 40), false, false, false, attack: false);
        Assert.Equal(2, s.EnergyShield.Count);
        Assert.Equal(40, s.TakeRecovery(1).EnergyShield);
        s.ClearFull(1, false, false, true);
        Assert.Equal(1, s.EnergyShield.Count);
        Assert.Equal(20, s.TakeRecovery(2).EnergyShield);
        Assert.Equal(60, s.TakeRecovery(10).EnergyShield);
        Assert.Equal(0, s.EnergyShield.Count);
    }

    [Fact]
    public void Attack_and_spell_instances_share_one_cap_before_and_after_full_pool_removal()
    {
        var s = new LeechSession();
        Configure(s, 0, new() { [Attacks] = 1 });
        for (int i = 0; i < 12; i++) s.Hit(0, (0, 0, 100), false, false, false, attack: i < 6);
        Assert.Equal(100, s.TakeRecovery(1).EnergyShield);
        s.ClearFull(1, false, false, true);
        Assert.Equal(6, s.EnergyShield.Count);
        Assert.Equal(100, s.TakeRecovery(2).EnergyShield);
        Configure(s, 2, []);
        s.ClearFull(2, false, false, true);
        Assert.Equal(0, s.EnergyShield.Count);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 2)]
    public void Ghost_Reaver_converted_and_native_instances_keep_the_hits_attack_origin(bool ghost, bool attack, int count)
    {
        var s = new LeechSession();
        Configure(s, 0, new() { [Attacks] = 1, ["keystone_ghost_reaver"] = ghost ? 1 : 0 });
        s.Hit(0, (100, 0, 100), false, false, true, attack);
        Assert.Equal(count, s.EnergyShield.Count);
        Assert.Equal(count * 20, s.TakeRecovery(1).EnergyShield);
    }

    [Fact]
    public void Gaining_retention_applies_to_existing_attack_instances_and_losing_it_waits_until_full()
    {
        var s = new LeechSession();
        Configure(s, 0, []);
        s.Hit(0, (0, 0, 100), false, false, false, attack: true);
        s.Hit(0, (0, 0, 100), false, false, false, attack: false);
        Configure(s, 1, new() { [Attacks] = 1 });
        s.ClearFull(1, false, false, true);
        Assert.Equal(1, s.EnergyShield.Count);
        Assert.Equal(0, s.TakeRecovery(1).EnergyShield); // Full-pool recovery cannot be banked.
        Configure(s, 2, []);
        s.ClearFull(2, false, false, false);
        Assert.Equal(1, s.EnergyShield.Count);
        s.ClearFull(3, false, false, true);
        Assert.Equal(0, s.EnergyShield.Count);
    }

    [Fact]
    public void General_ES_retention_keeps_spell_instances_too_and_expired_attacks_never_survive()
    {
        var s = new LeechSession();
        Configure(s, 0, new() { [Attacks] = 1, ["energy_shield_leech_does_not_stop_on_full_energy_shield"] = 1 });
        s.Hit(0, (0, 0, 20), false, false, true, attack: true);
        s.Hit(0, (0, 0, 100), false, false, true, attack: false);
        s.ClearFull(2, false, false, true);
        Assert.Equal(60, s.TakeRecovery(2).EnergyShield);
        Assert.Equal(1, s.EnergyShield.Count);
        Configure(s, 2, new() { [Attacks] = 1 });
        s.ClearFull(2, false, false, true);
        Assert.Equal(0, s.EnergyShield.Count);
    }
}

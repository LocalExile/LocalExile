namespace GameServer.Gateway.Tests;

public class LeechSessionTests
{
    static void Configure(LeechSession s, double now, Dictionary<string, int> raw, bool low = false)
    {
        int Get(string n) => raw.GetValueOrDefault(n);
        s.Configure(now, LeechLimits.Resolve(Get, 1000, 1000, 1000, onLowLife: low), Get, _ => null);
    }

    [Fact]
    public void Three_resources_have_separate_caps_and_full_pool_removal()
    {
        var s = new LeechSession(); Configure(s, 0, []);
        for (int i = 0; i < 12; i++) s.Hit(0, (100, 100, 100), false, false, false);
        Assert.Equal((200d, 200d, 100d), s.TakeRecovery(1));
        s.ClearFull(1, true, false, true);
        Assert.Equal((0d, 200d, 0d), s.TakeRecovery(2));
        s.Clear(2);
        Assert.Equal(default, s.TakeRecovery(100));
    }

    [Fact]
    public void Overleech_survives_full_pools_but_mana_never_banks_an_instance_at_full()
    {
        var s = new LeechSession();
        Configure(s, 0, new() { ["life_leech_does_not_stop_at_full_life"] = 1,
            ["energy_shield_leech_does_not_stop_on_full_energy_shield"] = 1 });
        s.Hit(0, (100, 100, 100), true, true, true);
        Assert.Equal((20d, 0d, 20d), s.TakeRecovery(1));
    }

    [Fact]
    public void Total_speed_recovery_rate_and_instant_portion_keep_distinct_meanings()
    {
        var s = new LeechSession();
        Configure(s, 0, new() { ["life_leech_speed_+%"] = 100, ["life_leech_speed_is_doubled"] = 1,
            ["life_recovery_rate_+%"] = 50, ["leech_%_is_instant"] = 25 });
        Assert.Equal((25d, 25d, 25d), s.Hit(0, (100, 100, 100), false, false, false));
        Assert.Equal((120d, 20d, 20d), s.TakeRecovery(1));
        Assert.Equal((330d, 55d, 55d), s.TakeRecovery(10)); // remaining 2.75 seconds; 3.75 total duration
    }

    [Fact]
    public void Cannot_leech_clears_only_matching_pools_while_cannot_recover_keeps_instances()
    {
        var s = new LeechSession(); Configure(s, 0, []);
        s.Hit(0, (100, 100, 100), false, false, false);
        Configure(s, 0, new() { ["cannot_leech_when_on_low_life"] = 1 }, true);
        Assert.Equal((0d, 0d, 20d), s.TakeRecovery(1));
        Configure(s, 1, new() { ["cannot_recover_energy_shield"] = 1 });
        Assert.Equal(1, s.EnergyShield.Count);
        Assert.Equal(default, s.TakeRecovery(2));
        Configure(s, 2, []);
        Assert.Equal((0d, 0d, 20d), s.TakeRecovery(3));
    }

    [Fact]
    public void Changing_speed_settles_elapsed_time_and_does_not_change_existing_duration()
    {
        var s = new LeechSession(); Configure(s, 0, []);
        s.Hit(0, (100, 0, 0), false, false, false);
        Configure(s, 2, new() { ["life_leech_speed_+%"] = 100 });
        Assert.Equal(160, s.TakeRecovery(10).Life); // 2*20 then 3*40
    }
}

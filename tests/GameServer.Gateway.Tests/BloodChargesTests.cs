using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class BloodChargesTests
{
    [Fact]
    public void The_stats_exist_and_the_buff_row_is_BuffDefinitions_blood_scythe_charge()
    {
        var stats = new StatCatalog(ChargePanelStatsTestsData.Folder);
        foreach (string name in new[] { BloodCharges.Current, BloodCharges.Maximum, BloodCharges.Show, BloodCharges.DamagePerCharge, BloodCharges.CostPerCharge })
            Assert.NotNull(stats.Key(name));
        using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ChargePanelStatsTestsData.Folder, "tables", "BuffDefinitions.json")));
        var row = d.RootElement[BloodCharges.BuffRow];
        Assert.Equal("blood_scythe_charge", row.GetProperty("Id").GetString());
        Assert.True(row.GetProperty("ShowCount").GetBoolean());
        Assert.True(row.GetProperty("CancelOnDeath").GetBoolean());
        Assert.Equal(BloodCharges.Current, stats.Name((uint)row.GetProperty("Current_StatsKey").GetInt32() + 1));
        Assert.Equal(BloodCharges.Maximum, stats.Name((uint)row.GetProperty("Maximum_StatsKey").GetInt32() + 1));
    }

    [Fact]
    public void More_is_one_multiplier_of_the_charges_held()
    {
        Assert.Equal(1.0, BloodCharges.More(15, 0), 9);
        Assert.Equal(1.15, BloodCharges.More(15, 1), 9);
        Assert.Equal(1.75, BloodCharges.More(15, 5), 9);
        Assert.Equal(2.0, BloodCharges.More(20, 5), 9);
        Assert.Equal(0.0, BloodCharges.More(-200, 1), 9);
        Assert.Equal(1.0, BloodCharges.More(15, -3), 9);
    }

    [Fact]
    public void A_use_takes_a_charge_per_kill_and_gives_one_when_any_enemy_survives()
    {
        Assert.Equal(1, BloodCharges.Settle(0, kills: 0, survivors: 3, maximum: 5));
        Assert.Equal(0, BloodCharges.Settle(0, kills: 2, survivors: 0, maximum: 5));
        Assert.Equal(2, BloodCharges.Settle(3, kills: 2, survivors: 1, maximum: 5));
        Assert.Equal(5, BloodCharges.Settle(5, kills: 0, survivors: 1, maximum: 5));
        Assert.Equal(0, BloodCharges.Settle(0, kills: 0, survivors: 0, maximum: 5));
        Assert.Equal(0, BloodCharges.Settle(3, kills: 0, survivors: 1, maximum: 0));
    }

    [Fact]
    public void Only_a_skill_with_the_per_charge_damage_stat_uses_them()
    {
        Assert.True(BloodCharges.Uses(n => n == BloodCharges.DamagePerCharge ? 15 : 0));
        Assert.False(BloodCharges.Uses(_ => 0));
    }
}

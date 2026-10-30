using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class ChargeBuffsTests
{
    [Fact]
    public void The_buff_rows_are_the_charge_rows_of_BuffDefinitions()
    {
        using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ChargePanelStatsTestsData.Folder, "tables", "BuffDefinitions.json")));
        var stats = new StatCatalog(ChargePanelStatsTestsData.Folder);
        foreach (var type in Charges.Types)
        {
            var row = d.RootElement[ChargeBuffs.Row(type)];
            Assert.Equal(ChargeBuffs.Id(type), row.GetProperty("Id").GetString());
            Assert.True(row.GetProperty("ShowCount").GetBoolean());
            Assert.True(row.GetProperty("CancelOnDeath").GetBoolean());
            // Neither IsRecovery nor the +0xff bool: the message has no stat values (11-skills.md §12).
            Assert.False(row.GetProperty("IsRecovery").GetBoolean());
            Assert.Equal($"actual_current_{Charges.Name(type)}_charges", stats.Name((uint)row.GetProperty("Current_StatsKey").GetInt32() + 1));
            Assert.Equal($"virtual_maximum_{Charges.Name(type)}_charges", stats.Name((uint)row.GetProperty("Maximum_StatsKey").GetInt32() + 1));
        }
    }

}

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

    [Fact]
    public void The_special_buff_rows_are_the_converted_charge_rows_of_BuffDefinitions()
    {
        using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ChargePanelStatsTestsData.Folder, "tables", "BuffDefinitions.json")));
        var stats = new StatCatalog(ChargePanelStatsTestsData.Folder);
        foreach (var type in Charges.Types)
        {
            var row = d.RootElement[ChargeBuffs.Row(type, converted: true)];
            Assert.Equal(ChargeBuffs.Id(type, converted: true), row.GetProperty("Id").GetString());
            Assert.Equal($"{Charges.SpecialName(type)}_charge", ChargeBuffs.Id(type, converted: true));
            Assert.True(row.GetProperty("ShowCount").GetBoolean());
            Assert.True(row.GetProperty("CancelOnDeath").GetBoolean());
            Assert.False(row.GetProperty("IsRecovery").GetBoolean());
            // These rows count current_*_charges itself, the Charges tab's row, not an actual_ alias.
            Assert.Equal($"current_{Charges.SpecialName(type)}_charges", stats.Name((uint)row.GetProperty("Current_StatsKey").GetInt32() + 1));
            Assert.Equal($"virtual_maximum_{Charges.SpecialName(type)}_charges", stats.Name((uint)row.GetProperty("Maximum_StatsKey").GetInt32() + 1));
        }
        var packet = ChargeBuffs.Packets(new ChargeSession.BuffChange(ChargeType.Endurance, 0, 2, 3, 9.5, Converted: true), 0x1000).Single();
        Assert.Contains("brutal_charge (BuffDefinitions row 1757)", packet.Label);
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0002" + "06DD" + "00001000" + "0000" + "06" + "41180000" + "0003" + "00000000",
            Convert.ToHexString(packet.Packet));
    }

}

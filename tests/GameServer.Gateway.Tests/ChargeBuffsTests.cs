// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class ChargeBuffsTests
{
    [Fact]
    public void The_buff_rows_are_the_charge_rows_of_BuffDefinitions()
    {
        using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "BuffDefinitions.json")));
        var stats = new StatCatalog("data");
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
    public void A_change_takes_the_old_buff_off_and_puts_the_new_count_and_time_on()
    {
        var packets = ChargeBuffs.Packets(new ChargeSession.BuffChange(ChargeType.Frenzy, Remove: 1, Add: 2, Count: 3, Remaining: 9.5), 0x1000)
            .Select(p => Convert.ToHexString(p.Packet)).ToList();
        Assert.Equal(["01C2" + "00001000" + "00000000" + "0000" + "0001",
            "01C1" + "00001000" + "00000000" + "0000" + "0002" + "001B" + "00001000" + "0000" + "06" + "41180000" + "0003" + "00000000"], packets);
    }

    [Fact]
    public void Minimum_charges_go_without_a_duration_and_none_left_sends_only_the_removal()
    {
        var minimum = ChargeBuffs.Packets(new ChargeSession.BuffChange(ChargeType.Power, 0, 5, 1, null), 0x1000).Single();
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0005" + "001D" + "00001000" + "0000" + "04" + "0001" + "00000000",
            Convert.ToHexString(minimum.Packet));
        var gone = ChargeBuffs.Packets(new ChargeSession.BuffChange(ChargeType.Endurance, 7, 0, 0, null), 0x1000).Single();
        Assert.Equal("01C2" + "00001000" + "00000000" + "0000" + "0007", Convert.ToHexString(gone.Packet));
    }
}

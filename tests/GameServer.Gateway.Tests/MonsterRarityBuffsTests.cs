using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MonsterRarityBuffsTests
{
    [Fact]
    public void Magic_and_rare_monsters_have_the_champion_glow_rows_of_BuffDefinitions()
    {
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ChargePanelStatsTestsData.Folder, "tables", "BuffDefinitions.json")));
        using var visuals = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ChargePanelStatsTestsData.Folder, "tables", "BuffVisuals.json")));
        foreach (var (rarity, colour) in new[] { (MonsterRarity.Magic, "blue"), (MonsterRarity.Rare, "gold") })
        {
            ushort row = MonsterRarityBuffs.Row(rarity)!.Value;
            var definition = definitions.RootElement[row];
            Assert.Equal(MonsterRarityBuffs.Id(row), definition.GetProperty("Id").GetString());
            Assert.True(definition.GetProperty("Invisible").GetBoolean());
            Assert.True(definition.GetProperty("CancelOnDeath").GetBoolean());
            // Neither IsRecovery nor any stats: the 0x1c1 carries no values.
            Assert.False(definition.GetProperty("IsRecovery").GetBoolean());
            Assert.Empty(definition.GetProperty("StatsKeys").EnumerateArray());
            var visual = visuals.RootElement[definition.GetProperty("BuffVisualsKey").GetInt32()];
            Assert.Contains($"Metadata/Effects/StatusAilments/monsters/champions/{colour}/{colour}.epk",
                visual.GetProperty("EPKFiles1").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Null(MonsterRarityBuffs.Row(MonsterRarity.Normal));
        Assert.Null(MonsterRarityBuffs.Row(MonsterRarity.Unique));
    }

    [Fact]
    public void The_glow_buff_message_has_no_optional_fields()
    {
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0007" + "0043" + "00000000" + "0000" + "00" + "00000000",
            Convert.ToHexString(InstanceEntities.AddBuff(0x1000, 7, MonsterRarityBuffs.RareRow)));
    }
}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The item tables' non-base resistance spellings count as the same character resistances.</summary>
public class ResistanceSpellingTests
{
    [Fact]
    public void Resistance_stats_without_the_base_prefix_reach_the_bonus()
    {
        var stats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("elemental_damage_resistance_+%")!.Value] = 20,
            [Program.Stats.Key("fire_damage_resistance_+%")!.Value] = -50,
            [Program.Stats.Key("cold_damage_resistance_+%")!.Value] = 5,
            [Program.Stats.Key("lightning_damage_resistance_+%")!.Value] = -5,
            [Program.Stats.Key("base_fire_damage_resistance_%")!.Value] = 7,
        };
        var bonus = Game.PassiveBonus(stats, default, default);
        Assert.Equal(20 - 50 + 7, bonus.FireResistance);
        Assert.Equal(20 + 5, bonus.ColdResistance);
        Assert.Equal(20 - 5, bonus.LightningResistance);
        Assert.Equal(0, bonus.ChaosResistance);
    }
}

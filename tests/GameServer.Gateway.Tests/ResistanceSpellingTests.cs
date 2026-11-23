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
            [Stat.ElementalDamageResistancePlusPct] = 20,
            [Stat.FireDamageResistancePlusPct] = -50,
            [Stat.ColdDamageResistancePlusPct] = 5,
            [Stat.LightningDamageResistancePlusPct] = -5,
            [Stat.BaseFireDamageResistancePct] = 7,
        };
        var bonus = Game.PassiveBonus(stats, default, default);
        Assert.Equal(20 - 50 + 7, bonus.FireResistance);
        Assert.Equal(20 + 5, bonus.ColdResistance);
        Assert.Equal(20 - 5, bonus.LightningResistance);
        Assert.Equal(0, bonus.ChaosResistance);
    }

    [Fact]
    public void Nearby_enemy_resistance_mods_do_not_reach_the_wearer()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items, "data");
        var stats = new StatCatalog();
        uint fire = Stat.FireDamageResistancePlusPct, cold = Stat.ColdDamageResistancePlusPct;
        var eye = uniques.Create(uniques.All.First(d => d.Name == "Eye of Malice"), new Random(1))!;
        var eyeStats = ItemModStats.Global([eye], items.Mods, stats);
        Assert.Equal(0, eyeStats.GetValueOrDefault(fire));
        Assert.Equal(0, eyeStats.GetValueOrDefault(cold));
        var cowl = uniques.Create(uniques.All.First(d => d.Name == "Cowl of the Cryophile"), new Random(1))!;
        var cowlStats = ItemModStats.Global([cowl], items.Mods, stats);
        Assert.True(cowlStats.GetValueOrDefault(fire) is <= -50 and >= -55, $"fire {cowlStats.GetValueOrDefault(fire)}");
    }
}

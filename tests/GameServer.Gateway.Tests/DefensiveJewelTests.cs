using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DefensiveJewelTests
{
    [Fact]
    public void Melding_shares_the_final_elemental_maximum_before_overcap_modifiers()
    {
        var jewel = new UniqueCatalog(Program.Items).Create("Melding of the Flesh", new Random(17))!;
        var sources = ItemModStats.Global([jewel], Program.Items.Mods, Program.Stats);
        sources[Program.Stats.Key("base_maximum_fire_damage_resistance_%")!.Value] = 25;
        sources[Program.Stats.Key("base_maximum_chaos_damage_resistance_%")!.Value] = 2;
        sources[Program.Stats.Key("base_cold_damage_resistance_%")!.Value] = 200;
        sources[Program.Stats.Key(ResistanceOvercap.ColdEvasionFlag)!.Value] = 1;
        var bonus = Game.PassiveBonus(sources, default, default);
        var result = ResistanceOvercap.Applied(bonus, sources, new Dictionary<uint, int>(), 0, Program.Stats);
        Assert.Equal(15, result.FireMaxResistance);
        Assert.Equal(15, result.ColdMaxResistance);
        Assert.Equal(15, result.LightningMaxResistance);
        Assert.Equal(2, result.ChaosMaxResistance);
        Assert.Equal(result.ColdResistance - 90, result.ColdOvercapEvasionPct);
    }
}

namespace GameServer.Gateway.Tests;

/// <summary>Tasalio's shared fire resistance, Valako's shared maximum, Hinekora's 500% life and Tukohama's supports.</summary>
public sealed class ChieftainNodesTests
{
    static Dictionary<uint, int> Keyed(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name)!.Value, s => s.Value);

    [Fact]
    public void Chieftain_notables_share_fire_resistance_and_add_body_armour_supports()
    {
        var bonus = Game.PassiveBonus(Keyed(
            ("base_fire_damage_resistance_%", 40),
            ("base_resist_all_elements_%", 20),
            (Chieftain.FireResistanceShare, 50),
            ("base_maximum_fire_damage_resistance_%", 4),
            ("additional_maximum_all_resistances_%", 1),
            (Chieftain.MaxFireResistanceShare, 1)), default, default);
        // Fire modifiers are 60. Half of that is added again on top of the 20 all-element resistance.
        Assert.Equal(60, bonus.FireResistance);
        Assert.Equal(50, bonus.ColdResistance);
        Assert.Equal(50, bonus.LightningResistance);
        // Maximum-fire modifiers are 5, and the +1 to all maximum resistances is already on cold and lightning.
        Assert.Equal(5, bonus.FireMaxResistance);
        Assert.Equal(6, bonus.ColdMaxResistance);
        Assert.Equal(6, bonus.LightningMaxResistance);
        Assert.Equal(-20, Chieftain.Shared(-40, 50));
        Assert.Equal(0, Chieftain.Shared(40, 0));
        Assert.Equal(5000, Chieftain.LifeAsFire(1000));

        var supports = Chieftain.BodyArmourSupports(name => name switch
        {
            Chieftain.AncestralCall => 30,
            Chieftain.FistOfWar => 20,
            _ => 0,
        }).ToList();
        Assert.Equal(2, supports.Count);
        Assert.Equal("SupportAncestralCall", Game.Supports.Of(supports[0].Effect)!.Id);
        Assert.Equal(30, supports[0].Level);
        Assert.Equal("SupportFistofWar", Game.Supports.Of(supports[1].Effect)!.Id);
        Assert.Equal(20, supports[1].Level);
        Assert.Equal(Chieftain.BodyArmourSupportSocket, supports[0].Socket);
        Assert.Empty(Chieftain.BodyArmourSupports(_ => 0));
    }
}

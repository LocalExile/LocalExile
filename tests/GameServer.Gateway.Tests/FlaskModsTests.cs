using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Flask mods on life and mana flasks and on every flask's charges (FlaskCharges, FlaskRecovery), with the numbers of
// Path of Building's Item.lua / CalcPerform.lua and poewiki's Flask page.
public class FlaskModsTests
{
    // Small Life Flask (Flasks.dat, ComponentCharges): 70 life over 3 s, 7 of 21 charges per use; Small Mana Flask 50 mana.
    static readonly Flasks.Flask SmallLife = new(70, 0, 3.0, 7, 21), SmallMana = new(0, 50, 3.0, 6, 24);
    static Item Flask(byte quality = 0) => new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Quality = quality };
    static Func<string, int> Character(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);
    static readonly Func<string, int> None = Character();
    static Dictionary<string, int> Local(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);

    [Fact]
    public void Charges_used_and_maximum_charges_follow_the_flask_and_the_character()
    {
        Assert.Equal(SmallLife, FlaskCharges.Of(SmallLife, Local()));
        // Chemist's 28% reduced charges used: floor(7 × 0.72) = 5; Ample +35 maximum charges.
        var f = FlaskCharges.Of(SmallLife, Local(("local_charges_used_+%", -28), ("local_extra_max_charges", 35)));
        Assert.Equal((5, 56), (f.ChargesPerUse, f.MaxCharges));
        // The tree's 5% reduced flask charges used multiplies after the flask's own: floor(floor(30 × 0.72) × 0.95) = 19.
        var quicksilver = new Flasks.Flask(0, 0, 6, 30, 60);
        Assert.Equal(19, FlaskCharges.Of(quicksilver, Local(("local_charges_used_+%", -28)), Character(("flask_charges_used_+%", -5))).ChargesPerUse);
        // Nitrate's 25% increased charges used.
        Assert.Equal(8, FlaskCharges.Of(SmallLife, Local(("local_charges_used_+%", 25))).ChargesPerUse);
    }

}

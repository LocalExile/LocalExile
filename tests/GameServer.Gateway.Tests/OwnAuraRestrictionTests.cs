using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class OwnAuraRestrictionTests
{
    internal static readonly string[] Ordinary = ["PurityOfElements", "PurityOfFire", "PurityOfIce",
        "PurityOfLightning", "Clarity", "Vitality", "Precision", "Haste", "Determination", "Grace",
        "Discipline", "Anger", "Hatred", "Wrath", "Zealotry", "Malevolence", "Envy"];

    [Fact]
    public void Actual_Aura_metadata_and_native_flags_do_not_classify_reservation_or_buff_names()
    {
        foreach (string id in Ordinary.Concat(["Pride", "FleshAndStone", "VaalHaste", "WarBanner"]))
        {
            int effect = Program.Skills.EffectRow(id);
            Assert.True(Program.Skills.IsAura(effect), id);
            var stats = Program.Skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(1, stats[OwnAuraRestriction.BaseAura]);
            Assert.Equal(1, stats[OwnAuraRestriction.Aura]);
        }
        foreach (string id in new[] { "HeraldOfAsh", "HeraldOfIce", "HeraldOfThunder", "ArcticArmour",
            "TempestShield", "PetrifiedBlood", "BloodAndSand", "RighteousFire", "MoltenShell", "EnduringCry",
            "Fireball", "PlayerMelee" })
        {
            int effect = Program.Skills.EffectRow(id);
            Assert.False(Program.Skills.IsAura(effect), id);
            Assert.DoesNotContain(Program.Skills.EffectStats(effect, 1),
                s => s.Item1 is OwnAuraRestriction.BaseAura or OwnAuraRestriction.Aura && s.Item2 != 0);
        }
        Assert.Null(Program.Skills.Reservation(Program.Skills.EffectRow("VaalHaste"), 1));
        Assert.NotNull(Program.Skills.Reservation(Program.Skills.EffectRow("BloodAndSand"), 1));
        Assert.False(Program.Skills.IsAura(-1));
        Assert.False(Program.Skills.IsAura(int.MaxValue));
    }

}

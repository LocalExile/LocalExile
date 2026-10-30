using System.Numerics;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class PowerOfPurposeTests
{
    [Theory]
    [InlineData(3, 0, 15, 33, 24u, 412)]
    public void Scaled_attributes_feed_Strength_groups_and_unrounded_Intelligence_before_conversion(
        int strengthPct, int intelligencePct, int strength, int intelligence, uint mana, int armour)
    {
        var bonus = new CharacterStats.Bonus(Mana: 9, ManaPct: 33, ManaPerFourStrength: 8,
            Armour: 100, ArmourPct: 50, MentalConditioning: true,
            Attributes: new(Strength: 1, Intelligence: 1, StrengthPct: strengthPct, IntelligencePct: intelligencePct));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus);
        Assert.Equal((strength, intelligence), (pools.Strength, pools.Intelligence));
        // At 16 Str / 33 Int: B = 34 + 6 + 9 + 16.5 + 32; armour = 150 + B * 1.6 * 1.83.
        Assert.Equal(mana, pools.Mana);
        Assert.Equal(armour, pools.Armour);
        Assert.Equal(pools, CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus));
        var ordinary = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus with { MentalConditioning = false });
        Assert.Equal((strength, intelligence), (ordinary.Strength, ordinary.Intelligence));
        Assert.Equal(150, ordinary.Armour);
    }

}

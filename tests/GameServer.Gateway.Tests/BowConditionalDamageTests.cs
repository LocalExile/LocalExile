using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>An item's "Adds # to # ... Damage with Bows" only reaches a bow's attacks (both table spellings).</summary>
public class BowConditionalDamageTests
{
    [Fact]
    public void Bow_conditional_added_damage_applies_only_to_bow_attacks()
    {
        var stats = new Dictionary<uint, int>
        {
            [Stat.AttackMinimumAddedPhysicalDamageWithBow] = 6,
            [Stat.AttackMaximumAddedPhysicalDamageWithBow] = 10,
            [Stat.AttackMinimumAddedColdDamageWithBows] = 100,
            [Stat.AttackMaximumAddedColdDamageWithBows] = 160,
        };
        int Get(uint name) => stats.GetValueOrDefault(name);
        var bow = SupportedAttackDamage.Raw(new SkillCatalog.Unarmed(0, 0, 0, 0) { Bow = true }, Get);
        Assert.Equal(6, bow[DamageType.Physical].Min);
        Assert.Equal(10, bow[DamageType.Physical].Max);
        Assert.Equal(100, bow[DamageType.Cold].Min);
        Assert.Equal(160, bow[DamageType.Cold].Max);
        var sword = SupportedAttackDamage.Raw(new SkillCatalog.Unarmed(0, 0, 0, 0), Get);
        Assert.Equal(0, sword[DamageType.Physical].Min);
        Assert.Equal(0, sword[DamageType.Cold].Max);
    }
}

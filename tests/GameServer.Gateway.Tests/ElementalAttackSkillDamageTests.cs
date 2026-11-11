using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>"Increased Elemental Damage with Attack Skills" (Roth's Reach, Ewar's Mirage, Aegis Aurora) counts on
/// attacks, not spells.</summary>
public class ElementalAttackSkillDamageTests
{
    [Fact]
    public void Elemental_damage_with_attack_skills_applies_to_attacks_only()
    {
        int Get(string name) => name == "elemental_damage_with_attack_skills_+%" ? 100 : 0;
        var raw = DamagePacket.Of(DamageType.Fire, 100, 100);
        var attack = PlayerHitDamage.Resolve(raw, Get, new DamageConversion(), spell: false);
        Assert.Equal(200, attack[DamageType.Fire].Min);
        var spell = PlayerHitDamage.Resolve(raw, Get, new DamageConversion(), spell: true);
        Assert.Equal(100, spell[DamageType.Fire].Min);
    }
}

namespace GameServer.Gateway.Tests;

public sealed class ShieldChargeCatalogTests
{
    [Fact]
    public void ShieldSkillsCarryTheirOwnAttackTimeAndOnlyShieldMovementSkillsCharge()
    {
        // The facts Shield Charge's charge runs on (11-skills.md §10k): a shield skill swings the shield with the gem's own
        // GrantedEffectsPerLevel AttackTime, which the off hand's attack time takes, and only a Movement skill that needs a
        // shield walks its charge instead of blinking.
        var skills = new SkillCatalog();
        int charge = skills.EffectRow("ShieldCharge"), crush = skills.EffectRow("ShieldCrush");
        int leap = skills.EffectRow("LeapSlam"), fireball = skills.EffectRow("Fireball");
        Assert.True(charge >= 0 && crush >= 0 && leap >= 0 && fireball >= 0);

        Assert.True(skills.RequiresShield(charge) && skills.IsMovement(charge) && skills.IsAttack(charge));
        Assert.Equal(500, skills.AttackTimeMs(charge, 1));
        Assert.Equal(500, skills.AttackTimeMs(charge, 20));
        Assert.True(skills.RequiresShield(crush) && !skills.IsMovement(crush));
        Assert.Equal(800, skills.AttackTimeMs(crush, 1));

        // Weapon skills keep their weapon's time, and Leap Slam blinks.
        Assert.False(skills.RequiresShield(leap));
        Assert.True(skills.IsMovement(leap));
        Assert.Equal(0, skills.AttackTimeMs(leap, 1));
        Assert.False(skills.RequiresShield(fireball));
        Assert.Equal(0, skills.AttackTimeMs(fireball, 1));
    }
}

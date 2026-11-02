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

    [Fact]
    public void ShieldSkillsCarryTheShieldDamageTheirHitsSwing()
    {
        // A shield skill's hit (ShieldWeapon, 11-skills.md §10k) is the gem's own added damage plus its damage per 15 of the
        // shield's armour and evasion, and Shield Charge's hits gain up to 100% more by the distance charged.
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        int Stat(string effect, int level, string name) => skills.EffectStats(skills.EffectRow(effect), level)
            .FirstOrDefault(s => s.Item1 == stats.Key(name)).Item2;
        string[] names = ["off_hand_local_minimum_added_physical_damage", "off_hand_local_maximum_added_physical_damage",
            "off_hand_minimum_added_physical_damage_per_15_shield_armour_and_evasion_rating",
            "off_hand_maximum_added_physical_damage_per_15_shield_armour_and_evasion_rating"];
        Assert.Equal([9, 13, 3, 5], names.Select(n => Stat("ShieldCharge", 1, n)));
        Assert.Equal([252, 378, 8, 12], names.Select(n => Stat("ShieldCharge", 20, n)));
        Assert.Equal([300, 450, 8, 12], names.Select(n => Stat("ShieldCrush", 20, n)));
        Assert.Equal(100, Stat("ShieldCharge", 20, "shield_charge_damage_+%_maximum"));
    }
}

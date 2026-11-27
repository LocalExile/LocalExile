namespace GameServer.Gateway.Tests;

public sealed class IceShotProjectileTests
{
    [Fact]
    public void Ice_shot_resolves_a_damaging_arrow_with_its_native_conversion_and_extra_arrows()
    {
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        int effect = skills.EffectRow("IceShot");
        Assert.True(effect >= 0 && skills.IsProjectile(effect));
        var projectile = skills.ProjectileFor(effect);
        Assert.NotNull(projectile);
        Assert.Equal("Metadata/Projectiles/IceArrow", projectile.Value.Id);
        Assert.Equal(3260, projectile.Value.Speed);
        var hit = SkillHitPlan.ForSkill(skills, effect, 1);
        Assert.True(hit.CanHit);
        Assert.Equal(SkillHitKind.Attack, hit.Kind);
        Assert.Contains(skills.EffectStats(effect, 1), s =>
            s.Item1 == Stat.SkillPhysicalDamagePctToConvertToCold && s.Item2 == 60);
        var definition = Game.CatalogProjectileDefinition(effect);
        Assert.NotNull(definition);
        Assert.Equal(projectile.Value.Id, definition.Metadata);
        var profile = definition.Parameters.Resolve(key => key == Stat.NumberOfAdditionalArrows ? 3 : 0, 326, true);
        Assert.Equal(4, profile.Count);
        Assert.Null(profile.EndArea);
        Assert.NotNull(profile.ContactArea);
        Assert.Equal(1, profile.ContactArea.Count);
        Assert.True(profile.ContactArea.ExcludeContactTarget);
        Assert.True(profile.ContactArea.CentreOnContactTarget);
    }

    [Fact]
    public void Penetration_has_sideways_contact_cones_and_no_explosion_when_an_arrow_expires()
    {
        var profile = GatewayProjectileSkillProfiles.Resolve("IceShotAltX", "Metadata/Projectiles/IceArrow",
            key => key == Stat.ActiveSkillBaseAreaOfEffectRadius ? 26 : key == Stat.SkillAreaAnglePlusPct ? 80 : 0,
            326, attack: true, arrows: true);
        Assert.Null(profile.EndArea);
        var cone = Assert.IsType<GameServer.Core.Projectiles.ProjectileAreaRecipe>(profile.ContactArea);
        Assert.Equal(2, cone.Count);
        Assert.Equal(26, cone.Radius);
        Assert.Equal(Math.PI / 4 * 1.8, cone.ConeHalfAngle, 6);
        Assert.Equal(Math.PI / 2, cone.ConeAxisRotation);
        Assert.Equal(Math.PI, cone.ConeAxisStep);
    }
}

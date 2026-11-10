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
            s.Item1 == stats.Key("skill_physical_damage_%_to_convert_to_cold") && s.Item2 == 60);
        var definition = Game.CatalogProjectileDefinition(effect);
        Assert.NotNull(definition);
        Assert.Equal(projectile.Value.Id, definition.Metadata);
        var profile = definition.Parameters.Resolve(key => key == "number_of_additional_arrows" ? 3 : 0, 326, true);
        Assert.Equal(4, profile.Count);
    }
}

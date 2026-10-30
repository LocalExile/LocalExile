namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Fact]
    public void Metadata_free_projectile_parameters_preserve_the_skill_return_prohibition()
    {
        using var f = new Fixture("MagmaOrb");
        int effect = f.Effect("RollingMagma");
        Assert.True(Program.Skills.IsProjectile(effect));
        Assert.Null(Program.Skills.ProjectileFor(effect));
        LinkProjectileSupports(f, "ReturningProjectiles");
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Single(skill.Supports);
        Assert.Equal(0, ProjectilePanelValue(skill, "projectiles_return"));
        Assert.Equal(0, ProjectilePanelValue(skill, "projectile_return_%_chance"));
        Assert.Equal(0, ProjectilePanelValue(skill, "returning_projectiles_always_pierce"));
        Assert.Equal(1, ProjectilePanelValue(skill, "skill_projectiles_cannot_return"));
        Assert.Null(Call("ResolveProjectileDefinition", effect));
    }

}

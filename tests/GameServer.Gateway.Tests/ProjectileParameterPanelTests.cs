namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Theory]
    [InlineData(false, false, 5, 2)]
    [InlineData(true, false, 1, 6)]
    [InlineData(true, true, 1, 0)]
    public async Task Projectile_parameters_project_split_conversion_and_prohibition_from_the_release_profile(
        bool conversion, bool cannotSplit, int primary, int split)
    {
        using var f = new Fixture("Fireball");
        LinkProjectileSupports(f, "GreaterMultipleProjectiles");
        f.Conn.PlayerStats[Program.Stats.Key("projectile_number_to_split")!.Value] = 2;
        f.Conn.PlayerStats[Program.Stats.Key("modifiers_to_number_of_projectiles_instead_apply_to_splitting")!.Value] = conversion ? 1 : 0;
        f.Conn.PlayerStats[Program.Stats.Key("projectiles_cannot_split")!.Value] = cannotSplit ? 1 : 0;
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(primary, ProjectilePanelValue(skill, "total_number_of_projectiles_to_fire"));
        Assert.Equal(split, ProjectilePanelValue(skill, "virtual_projectile_number_to_split"));
        Assert.Equal(cannotSplit ? 1 : 0, ProjectilePanelValue(skill, "projectiles_cannot_split"));
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        var profile = CapturePanelShot(f, runtime, "Fireball").Profile;
        Assert.Equal(primary, profile.Count);
        Assert.Equal(cannotSplit, profile.CannotSplit);
        Assert.Equal(split, profile.CannotSplit ? 0 : profile.Split);
    }

    [Theory]
    [InlineData("Fireball", "attack_projectiles_return", 1, false, 0, 0)]
    [InlineData("KineticBlast", "attack_projectiles_return", 1, false, 1, 0)]
    [InlineData("Fireball", "projectile_return_%_chance", 35, false, 0, 35)]
    [InlineData("Fireball", "projectile_return_%_chance", 150, false, 1, 100)]
    [InlineData("Fireball", "projectile_return_%_chance", 35, true, 0, 0)]
    [InlineData("Fireball", "projectiles_return", 1, true, 0, 0)]
    public async Task Projectile_parameters_project_scoped_chance_and_prohibited_return(
        string effect, string stat, int value, bool cannotReturn, int returns, int chance)
    {
        using var f = new Fixture(effect);
        f.Conn.PlayerStats[Program.Stats.Key(stat)!.Value] = value;
        f.Conn.PlayerStats[Program.Stats.Key("skill_projectiles_cannot_return")!.Value] = cannotReturn ? 1 : 0;
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(returns, ProjectilePanelValue(skill, "projectiles_return"));
        Assert.Equal(chance, ProjectilePanelValue(skill, "projectile_return_%_chance"));
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        var profile = CapturePanelShot(f, runtime, effect).Profile;
        Assert.Equal(cannotReturn, profile.CannotReturn);
        Assert.Equal(returns != 0, !profile.CannotReturn && profile.Returns);
        Assert.Equal(chance, profile.CannotReturn ? 0 : profile.ReturnChancePercent);
    }

    [Fact]
    public async Task Projectile_parameters_clear_return_and_return_pierce_when_a_linked_grant_is_prohibited()
    {
        using var f = new Fixture("Fireball");
        LinkProjectileSupports(f, "ReturningProjectiles");
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(1, ProjectilePanelValue(skill, "projectiles_return"));
        Assert.Equal(1, ProjectilePanelValue(skill, "returning_projectiles_always_pierce"));
        f.Conn.PlayerStats[Program.Stats.Key("skill_projectiles_cannot_return")!.Value] = 1;
        skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(0, ProjectilePanelValue(skill, "projectiles_return"));
        Assert.Equal(0, ProjectilePanelValue(skill, "returning_projectiles_always_pierce"));
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        Assert.True(CapturePanelShot(f, runtime, "Fireball").Profile.CannotReturn);
    }

    [Fact]
    public void Metadata_free_projectile_skill_has_generic_panel_parameters_without_an_executable_definition()
    {
        using var f = new Fixture("IceShot");
        int effect = f.Effect("IceShot");
        Assert.True(Program.Skills.IsProjectile(effect));
        Assert.Null(Program.Skills.ProjectileFor(effect));
        Assert.Null(Program.CatalogProjectileDefinition(effect));
        LinkProjectileSupports(f, "GreaterMultipleProjectiles", "Chain", "Pierce");
        f.Conn.PlayerStats[Program.Stats.Key("projectile_number_to_split")!.Value] = 2;
        f.Conn.PlayerStats[Program.Stats.Key("attack_projectiles_return")!.Value] = 1;
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(5, ProjectilePanelValue(skill, "total_number_of_projectiles_to_fire"));
        Assert.Equal(2, ProjectilePanelValue(skill, "virtual_number_of_chains_for_projectiles"));
        Assert.Equal(2, ProjectilePanelValue(skill, "projectile_number_of_targets_to_pierce"));
        Assert.Equal(2, ProjectilePanelValue(skill, "virtual_projectile_number_to_split"));
        Assert.Equal(1, ProjectilePanelValue(skill, "projectiles_return"));
        Assert.Null(Call("ResolveProjectileDefinition", effect));
        Assert.False((bool)Call("ProjectilePathAvailable", "panel-only", f.Conn, f.Skill(), effect)!);
    }
}

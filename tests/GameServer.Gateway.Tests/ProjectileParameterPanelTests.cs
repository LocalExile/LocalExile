namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Theory]
    [InlineData(false, false, 5, 2)]
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

}

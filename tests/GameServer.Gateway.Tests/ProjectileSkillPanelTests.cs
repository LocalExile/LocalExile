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

    [Theory]
    [InlineData("KineticBlast", 1, 4, 0)]
    [InlineData("KineticBlastAltX", 1, 3, 0)]
    [InlineData("KineticBlast", 1, 5, 20)]
    [InlineData("KineticBlastAltX", 1, 4, 20)]
    public async Task Projectile_panel_KB_explosion_quantity_matches_its_resolved_stage_before_and_after_GMP(
        string effect, int primary, int explosions, int quality)
    {
        using var f = new Fixture("KineticBlast");
        f.Conn.PlayerStats[Program.Stats.Key("projectile_number_to_split")!.Value] = 2;
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
            { Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = (byte)quality } }] });
        if (effect != "KineticBlast")
        {
            var type = Program.Items.Find("Metadata/Items/Gems/SkillGemKineticBlast")!;
            var variant = Program.Items.Gem(type)!.Variants.Single(v => v.Effect == f.Effect(effect));
            f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
                { Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { GemVariant = variant.Index } }] });
        }
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        AssertPanel(primary, explosions);
        LinkProjectileSupports(f, "GreaterMultipleProjectiles");
        AssertPanel(effect == "KineticBlast" ? 5 : 1, explosions + (effect == "KineticBlast" ? 0 : 4));
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with { Links = [1, 1] });
        AssertPanel(primary, explosions);

        void AssertPanel(int expectedPrimary, int expectedExplosions)
        {
            var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
                Call("GemSkillEffects", f.Conn.Character!)!).Single(g => g.Effect == f.Effect(effect));
            var skill = Skills(f.Conn).Single(s => s.Id == gem.Id);
            Assert.Equal(expectedPrimary, ProjectilePanelValue(skill, "total_number_of_projectiles_to_fire"));
            Assert.Equal(expectedExplosions, ProjectilePanelValue(skill, "virtual_kinectic_blast_number_of_clusters"));
            Assert.Equal(effect == "KineticBlastAltX" ? 0 : 2, ProjectilePanelValue(skill, "virtual_projectile_number_to_split"));
            Assert.Equal(effect == "KineticBlastAltX" ? 1 : 0, ProjectilePanelValue(skill, "projectiles_cannot_split"));
            var use = CapturePanelShot(f, runtime, effect);
            Assert.Equal(expectedPrimary, use.Profile.Count);
            Assert.Equal(expectedExplosions, use.Profile.ContactArea!.Count);
        }
    }
}

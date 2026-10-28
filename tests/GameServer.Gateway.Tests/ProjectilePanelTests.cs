using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    static void LinkProjectileSupports(Fixture f, params string[] supports) =>
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
        {
            Sockets = [item.Sockets[0], .. supports.Select(id => new ItemSocket(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGem" + id)!)))],
            Links = [(byte)(1 + supports.Length)]
        });

    static int ProjectilePanelValue(GrantedSkill skill, string stat) =>
        skill.Stats!.SingleOrDefault(s => s.Item1 == Program.Stats.Key(stat)).Item2;

    static ProjectileUse CapturePanelShot(Fixture f, ProjectileRuntimeFixture runtime, string effect)
    {
        var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn.Character!)!).Single(g => g.Effect == f.Effect(effect));
        return (ProjectileUse)Call("CaptureGemProjectile", f.Conn, runtime.Session, gem.Id, gem, (110, 32))!;
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    public async Task Projectile_panel_publishes_final_GMP_quantity_without_feeding_it_back_into_release(string effect)
    {
        using var f = new Fixture(effect);
        LinkProjectileSupports(f, "GreaterMultipleProjectiles");
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Single(skill.Supports);
        Assert.Equal(5, ProjectilePanelValue(skill, "total_number_of_projectiles_to_fire"));
        Assert.Equal(5, CapturePanelShot(f, runtime, effect).Profile.Count);
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with { Links = [1, 1] });
        skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Empty(skill.Supports);
        Assert.Equal(1, ProjectilePanelValue(skill, "total_number_of_projectiles_to_fire"));
        Assert.Equal(1, CapturePanelShot(f, runtime, effect).Profile.Count);
    }

    [Theory]
    [InlineData("Chain", "virtual_number_of_chains_for_projectiles", 2)]
    [InlineData("Fork", "virtual_number_of_forks_for_projectiles_final", 1)]
    [InlineData("Pierce", "projectile_number_of_targets_to_pierce", 2)]
    public async Task Projectile_panel_derives_continuation_rows_from_linked_supports(string support, string stat, int expected)
    {
        using var f = new Fixture("Fireball");
        LinkProjectileSupports(f, support);
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(expected, ProjectilePanelValue(skill, stat));
        Assert.Equal(expected, ReleaseCount());
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with { Links = [1, 1] });
        Assert.Equal(0, ProjectilePanelValue(Skills(f.Conn).Single(s => s.Id == f.Skill()), stat));
        Assert.Equal(0, ReleaseCount());

        int ReleaseCount()
        {
            var profile = CapturePanelShot(f, runtime, "Fireball").Profile;
            return support switch { "Chain" => profile.Chain, "Fork" => profile.Fork, _ => profile.Pierce };
        }
    }

    [Fact]
    public void Projectile_panel_prohibitions_override_grants_and_unlimited_pierce_uses_native_flag()
    {
        using var f = new Fixture("Fireball");
        LinkProjectileSupports(f, "Chain", "Fork", "Pierce");
        f.Conn.PlayerStats[Program.Stats.Key("projectiles_from_spells_cannot_chain")!.Value] = 1;
        f.Conn.PlayerStats[Program.Stats.Key("projectiles_from_spells_cannot_fork")!.Value] = 1;
        f.Conn.PlayerStats[Program.Stats.Key("always_pierce")!.Value] = 1;
        var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(0, ProjectilePanelValue(skill, "virtual_number_of_chains_for_projectiles"));
        Assert.Equal(0, ProjectilePanelValue(skill, "virtual_number_of_forks_for_projectiles_final"));
        Assert.Equal(1, ProjectilePanelValue(skill, "virtual_always_pierce"));
        f.Conn.PlayerStats[Program.Stats.Key("projectiles_from_spells_cannot_pierce")!.Value] = 1;
        skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
        Assert.Equal(0, ProjectilePanelValue(skill, "virtual_always_pierce"));
        Assert.Equal(0, ProjectilePanelValue(skill, "projectile_number_of_targets_to_pierce"));
        Assert.Equal(1, ProjectilePanelValue(skill, "virtual_projectiles_cannot_pierce"));
    }

    [Fact]
    public void Nonprojectile_skills_do_not_receive_projectile_panel_totals()
    {
        using var f = new Fixture("Fireball", "IceNova");
        f.Conn.PlayerStats[Program.Stats.Key("number_of_additional_projectiles")!.Value] = 4;
        var skills = Skills(f.Conn);
        Assert.Equal(5, ProjectilePanelValue(skills.Single(s => s.Id == f.Skill()), "total_number_of_projectiles_to_fire"));
        Assert.Equal(0, ProjectilePanelValue(skills.Single(s => s.Id == f.Skill(1)), "total_number_of_projectiles_to_fire"));
    }

    [Theory]
    [InlineData("Fireball", 2)]
    [InlineData("KineticBlast", 5)]
    public async Task Projectile_panel_normalizes_arrow_pierce_aliases_without_changing_release(string effect, int expectedPierce)
    {
        using var f = new Fixture(effect);
        LinkProjectileSupports(f, "Pierce");
        f.Conn.PlayerStats[Program.Stats.Key("arrow_number_of_targets_to_pierce")!.Value] = 3;
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        Check(expectedPierce, false, false);
        f.Conn.PlayerStats[Program.Stats.Key("arrows_always_pierce")!.Value] = 1;
        Check(effect == "KineticBlast" ? -1 : expectedPierce, false, effect == "KineticBlast");
        f.Conn.PlayerStats[Program.Stats.Key("cannot_pierce")!.Value] = 1;
        Check(effect == "KineticBlast" ? -1 : expectedPierce, true, false);

        void Check(int rawPierce, bool cannot, bool unlimited)
        {
            var skill = Skills(f.Conn).Single(s => s.Id == f.Skill());
            Assert.Equal(cannot || unlimited ? 0 : rawPierce, ProjectilePanelValue(skill, "projectile_number_of_targets_to_pierce"));
            Assert.Equal(unlimited ? 1 : 0, ProjectilePanelValue(skill, "virtual_always_pierce"));
            Assert.Equal(cannot ? 1 : 0, ProjectilePanelValue(skill, "virtual_projectiles_cannot_pierce"));
            Assert.Equal(0, ProjectilePanelValue(skill, "arrow_number_of_targets_to_pierce"));
            Assert.Equal(0, ProjectilePanelValue(skill, "arrows_always_pierce"));
            var profile = CapturePanelShot(f, runtime, effect).Profile;
            Assert.Equal(rawPierce, profile.Pierce);
            Assert.Equal(cannot, profile.CannotPierce);
        }
    }
}

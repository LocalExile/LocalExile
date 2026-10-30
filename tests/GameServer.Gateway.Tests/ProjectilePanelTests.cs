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
            Call("GemSkillEffects", f.Conn)!).Single(g => g.Effect == f.Effect(effect));
        return (ProjectileUse)Call("CaptureGemProjectile", f.Conn, runtime.Session, gem.Id, gem, (110, 32))!;
    }

    [Theory]
    [InlineData("Fireball")]
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

}

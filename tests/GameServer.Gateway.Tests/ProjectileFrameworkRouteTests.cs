using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{

    [Fact]
    public async Task A_projectile_without_catalog_metadata_is_refused_before_costs_or_area_damage()
    {
        using var f = new Fixture();
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id,
            _ => ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            {
                Sockets = [new(SocketColour.White,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemIceShot")!))]
            });
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        int effect = f.Effect("IceShot");
        Assert.True(Program.Skills.IsProjectile(effect));
        Assert.Null(Program.Skills.ProjectileFor(effect));
        Assert.Null(Call("GemSkillInstanceDisabled", f.Conn, effect, f.Skill()));
        var monster = new SpawnedMonster("unavailable projectile target", new(38, 32, 0), 0, 2, 10000);
        f.Conn.Monsters.Add(42, monster);
        uint mana = f.Conn.PlayerMana;

        await f.Use("ground");

        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(17, f.Conn.ActionCount);
        Assert.Equal(10000u, monster.Life);
        Assert.Empty(f.Conn.SkillReadyAt);
        Assert.Null(f.Conn.ProjectileRuntime);
    }
}

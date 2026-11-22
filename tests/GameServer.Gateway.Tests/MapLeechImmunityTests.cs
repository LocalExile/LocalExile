using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MapLeechImmunityTests
{
    [Fact]
    public void Map_monsters_deny_leech_while_allowing_life_gained_on_hit()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        conn.PlayerLife = 500;
        conn.PlayerMana = 500;
        conn.Pools = conn.Pools with { EnergyShield = 200 };
        var input = new MapMods(new Dictionary<string, int> { ["map_monsters_cannot_be_leeched_from"] = 100 });
        var monster = new SpawnedMonster("map leech immunity", new(10, 10, 0), 0, 0, 1000)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(Game.MapMonsterEffects(input)))
        };
        var damage = DamagePacket.Of(DamageType.Physical, 100, 100);
        var coefficient = DamagePacket.Of(DamageType.Physical, 5000, 5000);
        var sources = new LeechSources(coefficient, coefficient, coefficient)
        {
            Attack = true,
            GainOnHit = (17, 0, 0)
        };
        var leech = Game.LeechOf(conn);
        double now = Game.ProjectileWorldTime(conn);
        Game.ConfigureLeech(conn, name => name == "keystone_ghost_reaver" ? 1 : 0, new Dictionary<uint, int>(), onLowLife: true);
        Assert.True(leech.ConvertsLifeToEnergyShield);
        var ordinary = new SpawnedMonster("ordinary", new(10, 10, 0), 0, 0, 1000);
        Game.LeechOnHitLocked(conn, ordinary, damage, 100, sources);
        Assert.True(leech.Mana.Active(now));
        Assert.True(leech.EnergyShield.Active(now));
        leech.Clear(now);
        conn.PlayerLife = 500;
        Game.LeechOnHitLocked(conn, monster, damage, 100, sources);
        Assert.False(leech.Life.Active(now));
        Assert.False(leech.Mana.Active(now));
        Assert.False(leech.EnergyShield.Active(now));
        Assert.Equal(517u, conn.PlayerLife);
    }
}

using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AntiquarianProjectileTests
{
    [Theory]
    [InlineData(17, false, 60)]
    public async Task Real_projectile_contact_uses_travel_distance_and_never_scales_spells(int x, bool spell, uint life)
    {
        await using var f = new ProjectileRuntimeFixture();
        f.Conn.Monsters[2] = ProjectileRuntimeFixture.Monster(x, 10);
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 50, 50), spell,
            0, 0, 150, "Antiquarian Far Shot") { AlwaysHits = true, FarShot = true };
        f.Release(f.Shot(offense: offense)); f.Advance(10);
        Assert.Equal(life, f.Conn.Monsters[2].Life);
    }

}

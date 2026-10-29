using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AntiquarianProjectileTests
{
    [Theory]
    [InlineData(17, false, 60)]
    [InlineData(81, false, 20)]
    [InlineData(81, true, 50)]
    public async Task Real_projectile_contact_uses_travel_distance_and_never_scales_spells(int x, bool spell, uint life)
    {
        await using var f = new ProjectileRuntimeFixture();
        f.Conn.Monsters[2] = ProjectileRuntimeFixture.Monster(x, 10);
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 50, 50), spell,
            0, 0, 150, "Antiquarian Far Shot") { AlwaysHits = true, FarShot = true };
        f.Release(f.Shot(offense: offense)); f.Advance(10);
        Assert.Equal(life, f.Conn.Monsters[2].Life);
    }

    [Fact]
    public async Task Unknown_area_dimensions_do_not_erase_an_arrow_before_contact()
    {
        var conn = new ClientState(new(1, "uncaptured-antiquarian", 0, 0, 10, 10, null, []), 1, 1)
            { PlayerLife = 100 };
        await using var f = new ProjectileRuntimeFixture(conn);
        f.Conn.Monsters[2] = ProjectileRuntimeFixture.Monster(20, 10);
        f.Release(); f.Advance(2);
        Assert.Equal(90u, f.Conn.Monsters[2].Life);
    }
}

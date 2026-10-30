using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class PiercingProjectileTests
{
    static ProjectileProfile Arrow => new("Metadata/Projectiles/DefaultArrow")
        { Speed = 50, Range = 200, Radius = 1, Pierce = 2 };

    [Theory]
    [InlineData(false)]
    public async Task Piercing_contacts_use_current_moving_target_positions_after_launch(bool enters)
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(110, enters ? 150 : 10);
        f.Conn.Monsters.Add(42, target); f.Release(f.Shot(Arrow)); f.Advance(.3);
        // Explicit placement is a discontinuity, not a synthetic historical crossing.
        target.Walk.Teleport(110, enters ? 10 : 150, .3);
        f.Advance(4.01);
        Assert.Equal(enters ? 90u : 100u, target.Life);
        Assert.Empty(f.Session.Runtime.Projectiles);
    }

}

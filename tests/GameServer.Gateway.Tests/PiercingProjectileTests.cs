using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class PiercingProjectileTests
{
    static ProjectileProfile Arrow => new("Metadata/Projectiles/DefaultArrow")
        { Speed = 50, Range = 200, Radius = 1, Pierce = 2 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
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

    [Fact]
    public async Task A_paused_piercing_flight_freezes_its_horizon_then_hits_after_resume()
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(60, 10); f.Conn.Monsters.Add(42, target);
        f.Release(f.Shot(Arrow)); f.Advance(.15); f.Conn.Clock.Stop();
        long horizon = f.Session.Runtime.Now; var position = Assert.Single(f.Session.Runtime.Projectiles).Position;
        for (int i = 0; i < 20; i++) f.Session.Advance();
        Assert.Equal(horizon, f.Session.Runtime.Now);
        Assert.Equal(position, Assert.Single(f.Session.Runtime.Projectiles).Position);
        Assert.Equal(100u, target.Life);
        f.Conn.Clock.Start(); f.Advance(4.01); Assert.Equal(90u, target.Life);
    }

    [Fact]
    public async Task A_transfer_cancels_a_piercing_flight_before_contact()
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(40, 10); f.Conn.Monsters.Add(42, target);
        f.Release(f.Shot(Arrow)); f.Advance(.15);
        Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
        f.Advance(4.01);
        Assert.Equal(100u, target.Life); Assert.Empty(f.Session.Runtime.Projectiles);
        Assert.Empty(f.Conn.ProjectileObjects);
    }

    [Fact]
    public async Task Piercing_commits_each_real_hit_once_without_waiting_for_native_output()
    {
        await using var f = new ProjectileRuntimeFixture();
        var near = ProjectileRuntimeFixture.Monster(40, 10); var far = ProjectileRuntimeFixture.Monster(110, 10);
        f.Conn.Monsters.Add(9, far); f.Conn.Monsters.Add(2, near);
        await f.HoldOutput(); f.Release(f.Shot(Arrow));
        f.Advance(.7); Assert.Equal(90u, near.Life); Assert.Equal(100u, far.Life);
        f.Advance(4.01); f.Advance(5);
        Assert.Equal(90u, near.Life); Assert.Equal(90u, far.Life); Assert.Equal(0, f.Available);
    }
}

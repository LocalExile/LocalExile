using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileFlightTests
{
    static ProjectileProfile Arrow => new("test") { Speed = 10, Range = 100, Radius = 1, Lifetime = ProjectileRuntime.Time(20) };

    [Fact]
    public void An_evaded_near_contact_does_not_hide_a_later_hit_in_the_same_delayed_advance()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(9, 80, 10); world.Target(2, 40, 10);
        world.Accept = hit => hit.Target.Id == 9 ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        world.Release(); world.Advance(10.01);
        Assert.Equal(new uint[] { 2, 9 }, world.Attempts.Select(h => h.Target.Id));
        Assert.InRange(world.Attempts[1].At, ProjectileRuntime.Time(6.9) - 1, ProjectileRuntime.Time(6.9) + 1);
        Assert.InRange(world.Attempts[1].Position.X, 79, 79 + 10d / ProjectileRuntime.QuantaPerSecond + 1e-9);
        Assert.Empty(world.Runtime.Projectiles);
    }

}

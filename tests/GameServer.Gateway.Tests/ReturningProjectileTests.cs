using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ReturningProjectileTests
{
    [Fact]
    public async Task Return_damage_is_swept_at_contact_time_towards_the_live_anchor()
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(60, 10); f.Conn.Monsters.Add(42, target);
        var profile = new ProjectileProfile("Metadata/Projectiles/DefaultArrow")
            { Speed = 50, Range = 100, Radius = 1, Returns = true, Pierce = 2 };
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), true,
            int.MaxValue, 0, 150, "return") { ReturnMultiplier = .5 };
        f.Release(f.Shot(profile, offense) with { ReturnAnchor = new(Program.PlayerId, f.Conn.ProjectileEpoch) });
        f.Advance(1); Assert.Equal(90u, target.Life);
        f.Advance(2.001); Assert.Equal(1, Assert.Single(f.Session.Runtime.Projectiles).Phase);
        Assert.Equal(90u, target.Life); // Turning does not batch return damage.
        f.Advance(2.9); Assert.Equal(90u, target.Life);
        f.Advance(3.1); Assert.Equal(85u, target.Life);
        f.Advance(4); Assert.Equal(85u, target.Life); Assert.Empty(f.Session.Runtime.Projectiles);
    }

    [Fact]
    public async Task Return_uses_the_casters_position_at_turn_without_overshooting_it()
    {
        await using var f = new ProjectileRuntimeFixture();
        var behind = ProjectileRuntimeFixture.Monster(0, 10); f.Conn.Monsters.Add(42, behind);
        var profile = new ProjectileProfile("Metadata/Projectiles/DefaultArrow")
            { Speed = 50, Range = 100, Radius = 1, Returns = true };
        f.Release(f.Shot(profile) with { ReturnAnchor = new(Program.PlayerId, f.Conn.ProjectileEpoch) });
        f.Advance(1); f.Conn.Walk.Teleport(30, 10, 1); f.Advance(2.001);
        var returning = Assert.Single(f.Session.Runtime.Projectiles);
        Assert.Equal(1, returning.Phase); Assert.InRange(returning.Remaining, 79.9, 80);
        f.Advance(3.61); Assert.Empty(f.Session.Runtime.Projectiles); Assert.Equal(100u, behind.Life);
    }
}

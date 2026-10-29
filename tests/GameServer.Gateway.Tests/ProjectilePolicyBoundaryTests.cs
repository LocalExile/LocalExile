using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectilePolicyBoundaryTests
{
    [Theory]
    [InlineData(false, 21, 0, 0, 100u)]
    [InlineData(false, 20, 0, 0, 90u)]
    [InlineData(true, 21, 4, 17, 100u)]
    [InlineData(true, 19, 4, 25, 90u)]
    public async Task Close_range_accuracy_uses_target_centre_for_direct_and_secondary_area_hits(
        bool area, int targetDistanceX, int targetDistanceY, int areaDistance, uint expectedLife)
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(10 + targetDistanceX, 10 + targetDistanceY);
        f.Conn.Monsters.Add(42, target);
        // The first projectile's seeded accuracy roll is 57. Against level-one evasion 67,
        // accuracy 7 gives 50% hit chance; the real 50% close mastery raises rating to 10 and chance to 61%.
        // A radius-four direct contact occurs before the centre; an area victim can lie on either side of its centre.
        var profile = new ProjectileProfile("Metadata/Projectiles/DefaultArrow")
        {
            Speed = 10, Range = area ? areaDistance : 100, Radius = 4,
            DirectDamage = !area,
            EndArea = area ? new("secondary-area", 10) : null
        };
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Chaos, 10, 10), false,
            7, 0, 150, "accuracy-boundary") { AccuracyMoreClose = 50 };
        f.Release(f.Shot(profile, offense));
        f.Advance(3);
        Assert.Equal(expectedLife, target.Life);
    }

    [Fact]
    public void Partial_return_and_speed_modifiers_survive_binding_without_prematurely_granting_return()
    {
        int Get(string name) => name switch { "projectile_return_%_chance" => 50,
            "returned_projectile_speed_+%" => 75, _ => 0 };
        var profile = GatewayProjectileModifiers.Resolve(new("synthetic")
            { EndEmission = new(new("child")) }, Get, false);
        Assert.False(profile.Returns);
        Assert.Equal(50, profile.ReturnChancePercent);
        Assert.Equal(1.75, profile.ReturnSpeedMultiplier);
        Assert.Equal(50, profile.EndEmission!.Child.ReturnChancePercent);
        Assert.Equal(1.75, profile.EndEmission.Child.ReturnSpeedMultiplier);
    }
}

public partial class ProjectileSessionTests
{
    [Theory]
    [InlineData(false, 180000)]
    [InlineData(true, 42000)]
    [InlineData(false, 60000)]
    public async Task Effect_presentation_lifetime_controls_event_and_area_visual_retirement(bool areaVisual, long lifetime)
    {
        using var wire = new Wire(); var conn = Connection();
        var session = new Program.ProjectileSession("effect-lifetime", wire.Writer.GetStream(), wire.Ciphers, conn);
        async Task Drain()
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        try
        {
            var shot = Shot(conn) with { Profile = new("synthetic") { Speed = 1, Range = 1000, DirectDamage = false } };
            shot = shot with { Payload = new ProjectileOffense(DamagePacket.Empty, true, 0, 0, 150, "synthetic")
            {
                AreaVisual = areaVisual ? "synthetic-area.ao" : null,
                Presentation = new() { EffectLifetime = lifetime,
                    EventEffect = e => !areaVisual && e.Kind == ProjectileEventKind.Spawn ? "synthetic-event.ao" : null }
            } };
            session.Schedule(0, () => true, () => shot); session.AdvanceTo(0);
            if (areaVisual) session.Publish(new(ProjectileEventKind.Area, Assert.Single(session.Runtime.Projectiles), 0, new(10, 10)));
            await Drain(); Assert.Equal(2, conn.ProjectileObjects.Count);
            session.AdvanceTo(lifetime - 1); await Drain(); Assert.Equal(2, conn.ProjectileObjects.Count);
            session.AdvanceTo(lifetime); await Drain(); Assert.Single(conn.ProjectileObjects);
        }
        finally
        {
            conn.Closed = true; session.Invalidate();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

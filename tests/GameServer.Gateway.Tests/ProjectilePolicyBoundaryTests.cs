using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectilePolicyBoundaryTests
{
    [Theory]
    [InlineData(false, 21, 0, 0, 100u)]
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

}

public partial class ProjectileSessionTests
{
}

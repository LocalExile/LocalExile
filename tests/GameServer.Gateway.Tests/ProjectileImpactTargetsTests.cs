using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileImpactTargetsTests
{
    static SpawnedMonster Monster(int x, int y, int body = 0, bool hostile = true, bool dead = false, uint life = 100) =>
        new("impact target", new WalkEstimator(x, y, 30, null, 2000, 2000), 60, body, 100)
        { Hostile = hostile, Dead = dead, Life = life };

    [Fact]
    public void A_larger_explosion_hits_the_enemy_twelve_cells_away_and_does_not_duplicate_the_primary()
    {
        var primary = Monster(100, 100);
        var boundary = Monster(112, 100);
        var monsters = new Dictionary<uint, SpawnedMonster> { [1] = primary, [2] = boundary };
        Assert.Equal(new[] { 1u }, ProjectileImpactTargets.Explosion(monsters, (100, 100), 10, 0).Select(t => t.Id));
        var enlarged = ProjectileImpactTargets.Explosion(monsters, (100, 100), 13, 0);
        Assert.Equal(new[] { 1u, 2u }, enlarged.Select(t => t.Id));
        Assert.Single(enlarged, t => t.Monster == primary);
    }

    [Fact]
    public void Friendly_dead_and_zero_life_monsters_are_never_explosion_targets()
    {
        var monsters = new Dictionary<uint, SpawnedMonster>
        {
            [1] = Monster(100, 100), [2] = Monster(100, 100, hostile: false),
            [3] = Monster(100, 100, dead: true), [4] = Monster(100, 100, life: 0),
        };
        Assert.Equal(new[] { 1u }, ProjectileImpactTargets.Explosion(monsters, (100, 100), 10, 0).Select(t => t.Id));
    }

    [Fact]
    public void Body_radius_and_position_at_impact_are_used_for_overlap()
    {
        var large = Monster(112, 100, body: 2);
        var enters = Monster(120, 100);
        enters.Walk.Order(100, 100, 0);
        var monsters = new Dictionary<uint, SpawnedMonster> { [1] = large, [2] = enters };
        Assert.Equal(new[] { 1u }, ProjectileImpactTargets.Explosion(monsters, (100, 100), 10, 0).Select(t => t.Id));
        Assert.Equal(new[] { 1u, 2u }, ProjectileImpactTargets.Explosion(monsters, (100, 100), 10, 1).Select(t => t.Id));
    }
}

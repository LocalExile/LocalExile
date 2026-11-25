using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectileContactRegressionTests
{
    sealed class Arena : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [new(new(1), 1, _ => new(0, 0), 0)];
        public readonly List<ProjectileDamage> Hits = [];
        public readonly List<ProjectileEvent> Events = [];
        public Func<ProjectileDamage, ProjectileHitResult> Accept = _ => ProjectileHitResult.Hit;
        public Func<ProjectileVector, ProjectileVector, TerrainContact?> Terrain = (_, _) => null;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => Terrain(from, to);
        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            var result = Accept(damage);
            if (result == ProjectileHitResult.Hit) Hits.Add(damage);
            return result;
        }
        public void Publish(ProjectileEvent value) => Events.Add(value);
        public void Target(uint id, double x, double y = 0) => Targets.Add(new(new(id), 2, _ => new(x, y), 0));
    }
    static ProjectileProfile Basic => new("synthetic") { Speed = 100, Range = 100, Radius = 1 };
    static ProjectileUse Use(ProjectileProfile profile) => new(profile, new(1), new(1), 1, new(0, 0), new(100, 0))
        { ReturnAnchor = new(1) };

    [Theory]
    [InlineData(false, false)]
    public void Returning_chain_and_split_ignore_the_turnaround_actor_and_use_valid_alternatives(bool split, bool alternative)
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with
        {
            Returns = true, ExcludeTurnaroundTarget = true, ReturnOvershoot = 150,
            Chain = split ? 0 : 1, Split = split ? 1 : 0,
            EndArea = new("terminal", 2)
        }));
        runtime.AdvanceTo(36000);
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Phase);
        world.Target(3, 10);
        if (alternative) world.Target(4, 45, 20);
        runtime.AdvanceTo(120000);
        Assert.Single(world.Hits, h => h.Target.Id == 2); // The excluded actor receives only its outbound hit.
        var newlyEncountered = Assert.Single(world.Hits, h => h.Target.Id == 3);
        Assert.Equal(1, newlyEncountered.Phase);
        Assert.Equal(!alternative, newlyEncountered.IsArea);
        var terminal = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
        Assert.InRange(terminal.Position.X, alternative ? 44 : 10.999, alternative ? 46 : 11.001);
        if (alternative)
            Assert.True(Assert.Single(world.Hits, h => h.Target.Id == 4).IsArea);
        else
        {
            Assert.Equal("terminal", newlyEncountered.Stage);
            Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Redirect); // Only the turnaround.
            Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0);
        }
        Assert.Empty(runtime.Projectiles);
    }

}

using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectileExtensionTests
{
    [Fact]
    public void Count_consumers_apply_the_bonus_once_to_the_selected_stage()
    {
        var arrow = new ProjectileProfile("arrow");
        var modifiers = new ProjectileModifiers { ExtraProjectiles = 4, Chain = 2 };
        var ordinary = ProjectileParameters.Resolve(arrow, modifiers);
        Assert.Equal(5, ordinary.Count);
        var clustering = ProjectileParameters.Resolve(arrow with { CountConsumer = ProjectileCountConsumer.ContactAreas,
            ContactArea = new("blast", 10, 4) }, modifiers);
        Assert.Equal(1, clustering.Count);
        Assert.Equal(8, clustering.ContactArea!.Count);
        var bottle = ProjectileParameters.Resolve(arrow with { CountConsumer = ProjectileCountConsumer.GroundChains,
            GroundChains = 3 }, modifiers);
        Assert.Equal(1, bottle.Count);
        Assert.Equal(9, bottle.GroundChains);
    }

    sealed class Arena : IProjectileWorld
    {
        public List<ProjectileDamage> Hits = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) =>
            [new(new(2), 2, _ => new(30, 0), 0)];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) { Hits.Add(damage); return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) { }
    }

}

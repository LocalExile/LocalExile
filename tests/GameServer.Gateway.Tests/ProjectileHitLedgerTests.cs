using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class ProjectileHitLedgerTests
{
    sealed class Arena : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [new(new(1), 1, _ => new(0, 0), 0), new(new(2), 2, _ => new(5, 0), 0)];
        public readonly List<ProjectileDamage> Hits = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) { Hits.Add(damage); return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) { }
    }

    [Fact]
    public void Hit_ledger_is_pruned_only_after_a_flight_is_removed_and_never_repeats_a_hit()
    {
        var world = new Arena();
        var runtime = new ProjectileRuntime(world);
        var ledgerField = typeof(ProjectileRuntime).GetField("ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var ledger = (ProjectileHitLedger)ledgerField.GetValue(runtime)!;
        var profile = new ProjectileProfile("synthetic") { Speed = 10, Range = 50, Radius = 1, Pierce = 5 };
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)) { ReturnAnchor = new(1) });
        runtime.AdvanceTo(ProjectileRuntime.QuantaPerSecond);
        int hitsSoFar = world.Hits.Count;
        Assert.Equal(1, hitsSoFar);
        int tracked = ledger.Count;
        Assert.True(tracked > 0);
        // A key whose group no flight owns: the old per-advance prune dropped it on the next advance.
        ledger.TryHit(987654321, 0, "synthetic", new(1), 0, 0, new());
        Assert.Equal(tracked + 1, ledger.Count);
        for (long t = 31; t <= 100; t++) runtime.AdvanceTo(t * ProjectileRuntime.TickQuanta);
        Assert.Single(runtime.Projectiles);
        Assert.Equal(tracked + 1, ledger.Count); // No removal yet, so nothing was pruned.
        Assert.Equal(hitsSoFar, world.Hits.Count); // The same flight never hits the same target again.
        runtime.AdvanceTo(10 * ProjectileRuntime.QuantaPerSecond);
        Assert.Empty(runtime.Projectiles);
        Assert.Equal(0, ledger.Count); // The flight's entries and the stale one go once the flight is removed.
        Assert.Equal(hitsSoFar, world.Hits.Count);
    }
}

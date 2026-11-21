using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CombatCacheTests
{

    [Fact]
    public void An_adorned_jewel_is_scaled_once_so_the_stat_sources_cache_still_matches()
    {
        // The Adorned scales a corrupted magic jewel at every stat build; a new copy each time made every lookup miss.
        var jewel = new Item(1, [], 0) { Rarity = ItemRarity.Magic, Corrupted = true, Mods = [new(ModSection.Explicit, 1, [10])] };
        var catalog = Program.Items;
        var masteries = new Dictionary<ushort, ushort>();   // the key compares these by reference
        Game.StatSourcesKey Key(Item item) => new([], masteries, [new(1, 0, 0, item)], 0, false, 1,
            null, [], 0, [], new HashSet<string>(), new HashSet<string>(), null, 0, 0, 0, 100, 100, Catalog: catalog);

        var scaled = Game.AdornedJewel(jewel, 100);
        Assert.Equal([20], scaled.Mods[0].Values);
        Assert.Same(scaled, Game.AdornedJewel(jewel, 100));
        Assert.True(Key(scaled).Matches(Key(Game.AdornedJewel(jewel, 100))));
        // Another percentage, or another jewel, is scaled from its own values.
        var half = Game.AdornedJewel(jewel, 50);
        Assert.Equal([15], half.Mods[0].Values);
        Assert.False(Key(half).Matches(Key(scaled)));
        Assert.Equal([20], Game.AdornedJewel(jewel, 100).Mods[0].Values);
        Assert.Equal([60], Game.AdornedJewel(jewel with { Mods = [new(ModSection.Explicit, 1, [30])] }, 100).Mods[0].Values);
        Assert.Same(jewel, Game.AdornedJewel(jewel, 0));
        var uncorrupted = jewel with { Corrupted = false };
        Assert.Same(uncorrupted, Game.AdornedJewel(uncorrupted, 100));
    }

    [Fact]
    public void A_session_lists_its_actors_in_contact_order()
    {
        // The runtime decides contacts by id, then generation, and sorts a list that comes otherwise at every step.
        var player = new Player(new(1, "order", 1000, 1000, 100, 100, null, []), 1, 1) { PlayerLife = 100 };
        foreach (uint id in new[] { 0x3005u, 0x3001u, 0x3003u })
            player.Area.Monsters[id] = new("target", new(110, 100, 0), 0, 0, 100) { Hostile = true };
        player.Totems.Place(new TotemState.Totem(0x3004, 1, (100, 100), 100, 100, null), 2, 0);
        player.Totems.Place(new TotemState.Totem(0x3002, 1, (100, 100), 100, 100, null), 2, 0);
        var session = new Game.ProjectileSession(player);
        Assert.Equal(new[] { player.Id, 0x3001u, 0x3002u, 0x3003u, 0x3004u, 0x3005u }.Order(), session.Actors(0, 0).Select(a => a.Key.Id));
        player.PlayerLife = 0;
        Assert.Equal([0x3001u, 0x3002u, 0x3003u, 0x3004u, 0x3005u], session.Actors(0, 0).Select(a => a.Key.Id));
    }

    [Fact]
    public void A_session_keeps_its_actor_records_and_allocates_only_the_list_at_each_step()
    {
        // The runtime asks for the actors at every simulation step, thousands of times a second in a crowd.
        const int monsters = 400;
        var player = new Player(new(1, "records", 1000, 1000, 100, 100, null, []), 1, 1) { PlayerLife = 100 };
        for (uint i = 0; i < monsters; i++)
            player.Area.Monsters[0x3000 + 2 * i] = new("target", new(110, 100, 0), 0, 0, 100) { Hostile = true };
        player.Totems.Place(new TotemState.Totem(0x3001, 1, (100, 100), 100, 100, null), 2, 0);
        player.Totems.Place(new TotemState.Totem(0x3003, 1, (100, 100), 100, 100, null), 2, 0);
        var session = new Game.ProjectileSession(player);

        var first = session.Actors(0, 0);
        var second = session.Actors(0, 0);
        Assert.Equal(monsters + 3, first.Count);
        Assert.NotSame(first, second);   // the runtime holds one list while a hit changes the next
        for (int i = 0; i < first.Count; i++) Assert.Same(first[i], second[i]);
        // A monster whose walk was replaced gets a new record, and only that one.
        player.Area.Monsters[0x3000].Walk = new(120, 100, 0);
        var third = session.Actors(0, 0);
        Assert.Equal(1, Enumerable.Range(0, first.Count).Count(i => !ReferenceEquals(first[i], third[i])));

        // The list is a reference per actor. A closure per monster at every call was 24 bytes more each (49 KB a step
        // with 1000 monsters, 2026-11-16): allow twice the list, far below that.
        for (int i = 0; i < 50; i++) session.Actors(0, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int calls = 100;
        for (int i = 0; i < calls; i++) session.Actors(0, 0);
        long each = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
        Assert.InRange(each, 8 * monsters, 16 * monsters + 1024);
    }

    sealed class Arena : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [];
        public readonly List<ProjectileDamage> Hits = [];
        public int Reads, Snapshots;
        public Action<ProjectileDamage>? AfterHit;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) { Snapshots++; return Targets.ToArray(); }
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage hit) { Hits.Add(hit); AfterHit?.Invoke(hit); return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) { }
        public void Add(uint id, Func<long, ProjectileVector> position) =>
            Targets.Add(new(new(id), 2, t => { Reads++; return position(t); }, 0));
    }

    [Fact]
    public void Projectile_motion_is_shared_within_a_step_and_refreshed_after_contacts_and_callbacks()
    {
        var world = new Arena();
        for (uint id = 10; id < 110; id++) world.Add(id, _ => new(500, 500));
        var runtime = new ProjectileRuntime(world);
        var profile = new ProjectileProfile("cache") { Speed = 30, Range = 100, Radius = .1, Count = 40, SpreadRadians = 0 };
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)));
        runtime.AdvanceTo(ProjectileRuntime.TickQuanta);
        Assert.Empty(world.Hits);
        Assert.InRange(world.Reads, 1, world.Snapshots * world.Targets.Count * 2);

        world.Targets.Clear();
        // Lower ID wins a coincident contact. Its hit removes that actor and moves the other one away;
        // the remaining flights must not use the previous step's positions or retain the dead target.
        world.Add(20, _ => new(2, 0)); world.Add(10, _ => new(2, 0));
        world.AfterHit = _ => { world.Targets.Clear(); world.Add(30, _ => new(500, 500)); };
        runtime.AdvanceTo(ProjectileRuntime.Time(.1));
        Assert.Single(world.Hits);
        Assert.Equal(10u, world.Hits[0].Target.Id);
        world.AfterHit = null;
        runtime.Schedule(ProjectileRuntime.Time(.11), () => { world.Targets.Clear(); world.Add(40, _ => new(4, 0)); });
        runtime.AdvanceTo(ProjectileRuntime.Time(.2));
        Assert.Contains(world.Hits, h => h.Target.Id == 40);
    }
}

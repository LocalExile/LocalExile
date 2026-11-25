namespace GameServer.Gateway.Tests;

/// <summary>
/// A map's content that picks a monster "out of the area's packs" picks out of the packs the instance stands its
/// monsters from: the map's signature packs plus the instance's own draw from the shared map pool
/// (<c>Game.InstancePacks</c>, <see cref="AreaPopulation.ForInstance"/>). Forbidden Woods' Maps.dat list is the single
/// pack chaos-vendigo1 with one entry, so reading that list alone gives Vendigo for everything.
/// </summary>
public class InstancePackPicksTests
{
    const string Woods = "MapWorldsForbiddenWoods";

    [Fact]
    public void An_instance_draws_its_packs_from_the_shared_pool_and_every_caller_sees_the_same_draw()
    {
        var catalogue = Game.Packs.For(Woods)!;
        // The map's own list is the one single-entry pack: on its own it can only ever give Vendigo.
        Assert.Equal(["chaos-vendigo1"], catalogue.Scattered.Select(s => s.Pack.Id));
        Assert.Single(catalogue.Scattered.SelectMany(s => s.Pack.Entries).Select(e => e.Variety).Distinct());
        Assert.NotEmpty(catalogue.Pool);

        var instance = Game.InstancePacks(Woods, 0x1234, 7)!;
        // The signature packs first, then the draw; nothing left to draw.
        Assert.Empty(instance.Pool);
        Assert.Equal(catalogue.Scattered.Count + AreaPopulation.MapPoolPacks, instance.Scattered.Count);
        Assert.Equal(catalogue.Scattered.Select(s => s.Pack.Id), instance.Scattered.Take(catalogue.Scattered.Count).Select(s => s.Pack.Id));
        var drawn = instance.Scattered.Skip(catalogue.Scattered.Count).Select(s => s.Pack.Row).ToList();
        Assert.Equal(drawn.Count, drawn.Distinct().Count());
        Assert.All(drawn, row => Assert.Contains(row, catalogue.Pool.Select(p => p.Pack.Row)));
        Assert.True(instance.Scattered.SelectMany(s => s.Pack.Entries).Select(e => e.Variety).Distinct().Count() > 1);

        // Deterministic for (area, hash, seed); drawing again from the answer changes nothing; another seed draws another set.
        Assert.Equal(instance.Scattered.Select(s => s.Pack.Row), Game.InstancePacks(Woods, 0x1234, 7)!.Scattered.Select(s => s.Pack.Row));
        Assert.Same(instance, AreaPopulation.ForInstance(instance, AreaPopulation.PopulationSeed(0x1234, 7)));
        Assert.Contains(Enumerable.Range(8, 8), seed =>
            !Game.InstancePacks(Woods, 0x1234, (uint)seed)!.Scattered.Select(s => s.Pack.Row).SequenceEqual(instance.Scattered.Select(s => s.Pack.Row)));

        // The scattered population stands from the same set, drawn or not drawn beforehand.
        int seed0 = AreaPopulation.PopulationSeed(0x1234, 7);
        List<string> Standing(PackCatalog.AreaPacks area) => [.. AreaPopulation.Plan(area, 600, 600,
            (x, y) => x is > 5 and < 595 && y is > 5 and < 595, (50, 50), [], [], seed0, _ => true, [])
            .Select(s => s.Pack).Where(p => p.StartsWith("chaos-", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal)];
        Assert.All(Standing(instance), pack => Assert.Contains(pack, instance.Scattered.Select(s => s.Pack.Id)));
        Assert.Equal(Standing(instance), Standing(catalogue));

        // The player's own ask: without an instance it is drawn uncached, with one it is kept (the same reference twice),
        // and both are the draw the population makes with the production predicate.
        var layout = new AreaLayout(0x1234, Woods, 600, 600, 50, 50, (_, _) => true, []);
        var player = new Player(layout, 1, 7u);
        var uncached = Game.InstancePacks(player)!;
        player = new Player(layout, 1, 7u) { Instance = new InstanceState() };
        var kept = Game.InstancePacks(player)!;
        Assert.Same(kept, Game.InstancePacks(player));
        var production = AreaPopulation.ForInstance(catalogue, seed0, row => Game.Monsters.Find(row.ToString()) is { Supported: true });
        Assert.Equal(production.Scattered.Select(s => s.Pack.Row), kept.Scattered.Select(s => s.Pack.Row));
        Assert.Equal(production.Scattered.Select(s => s.Pack.Row), uncached.Scattered.Select(s => s.Pack.Row));
    }

    [Fact]
    public void A_maps_pool_is_cut_to_the_packs_that_fit_its_terrain()
    {
        // Forbidden Woods names the tag forestdark, which stands for forest: its pool is the forest packs and the untagged ones.
        var pool = Game.Packs.For(Woods)!.Pool;
        int forest = GameServer.Core.ClientTables.Table("Tags").Select((t, i) => (Id: t.TextOrNull("Id"), Row: i)).First(t => t.Id == "forest").Row;
        Assert.All(pool, p => Assert.True(p.Pack.Tags.Count == 0 || p.Pack.Tags.Contains(forest), p.Pack.Id));
        Assert.Contains(pool, p => p.Pack.Tags.Contains(forest));
        // A campaign area has no pool, and neither has a unique map area; an ordinary map with an empty signature list does.
        Assert.Empty(Game.Packs.For("1_1_2")!.Pool);
        Assert.Empty(Game.Packs.For("MapWorldsShapersRealm")!.Pool);
        Assert.Empty(Game.Packs.For("MapWorldsUltimatumBossArena")!.Pool);
        Assert.NotEmpty(Game.Packs.For("MapWorldsBazaar")!.Pool);
    }
}

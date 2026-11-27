using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A layout that gives an area no boss point at all still has the area's unique boss: it stands at the walk map's deepest
/// cell from the entrance, where a boss room would be, instead of standing nowhere. The fallback only fills in bosses no
/// room point brought, so a point that did bring its boss is not paid a second one.
/// </summary>
public class CampaignBossPlacementTests
{
    // Variety rows of WorldAreas' Bosses list; nothing here looks a variety up, so any row does.
    static PackCatalog.AreaPacks Area(params int[] bosses) =>
        new("1_1_11_2", 12, [], new Dictionary<string, PackCatalog.Pack>(), bosses);

    static bool Walk(int x, int y) => x is >= 0 and < 100 && y is >= 0 and < 100;

    [Fact]
    public void A_layout_without_a_boss_point_stands_its_unique_boss_deep_in_the_area()
    {
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(Area(1), 100, 100, Walk, (10, 10), [], [], 12345, _ => true, notes);

        var boss = Assert.Single(spawns);
        Assert.Equal(1, boss.Variety);
        Assert.Equal(MonsterRarity.Unique, boss.Rarity);
        Assert.Equal("mapboss", boss.Pack);
        Assert.True(Walk(boss.X, boss.Y));
        Assert.True(Math.Abs(boss.X - 10) + Math.Abs(boss.Y - 10) > 100, $"the boss stands at ({boss.X}, {boss.Y})");
        Assert.Contains(notes, note => note.Contains("standing at"));
    }

    [Fact]
    public void A_boss_its_room_point_already_brought_is_not_placed_twice()
    {
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(Area(1, 2), 100, 100, Walk, (10, 10), [], [new("mapboss1", 20, 20)], 7, _ => true, notes);

        Assert.Equal(2, spawns.Count);
        Assert.Single(spawns, spawn => spawn.Variety == 1);
        Assert.Single(spawns, spawn => spawn.Variety == 2);
    }

    [Fact]
    public void Partial_boss_points_ritual_override_and_warband_fallback_keep_bosses_off_arrival()
    {
        // MapWorldsPort-style: mapboss1 places boss 1, leaving bosses 3..5; they stay at mapboss1, not arrival.
        var portSpawns = AreaPopulation.Plan(Area(1, 3, 4, 5), 100, 100, Walk, (10, 10), [],
            [new("mapboss1", 80, 80)], 11, _ => true, []);
        Assert.Equal(4, portSpawns.Count);
        Assert.All(portSpawns, s => Assert.True(Math.Abs(s.X - 80) <= 12 && Math.Abs(s.Y - 80) <= 12,
            $"remaining boss spawned at ({s.X}, {s.Y}) instead of mapboss1 (80, 80)"));

        // MapWorldsGraveTrough-style: ritual_boss_spawn_override places the boss when no mapboss* exists.
        var ritualSpawns = AreaPopulation.Plan(Area(1), 100, 100, Walk, (10, 10), [],
            [new("ritual_boss_spawn_override", 75, 75)], 11, _ => true, []);
        var ritualBoss = Assert.Single(ritualSpawns);
        Assert.Equal((75, 75), (ritualBoss.X, ritualBoss.Y));

        // Warband points act as fallback only when no mapboss* point exists.
        var warbandSpawns = AreaPopulation.Plan(Area(1, 2), 100, 100, Walk, (10, 10), [],
            [new("mapbosswarband1", 70, 70), new("mapbosswarband2", 85, 85)], 11, _ => true, []);
        Assert.Equal(2, warbandSpawns.Count);
        Assert.Contains(warbandSpawns, s => s.Variety == 1 && (s.X, s.Y) == (70, 70));
        Assert.Contains(warbandSpawns, s => s.Variety == 2 && (s.X, s.Y) == (85, 85));

        var withMapBoss = AreaPopulation.Plan(Area(1), 100, 100, Walk, (10, 10), [],
            [new("mapboss", 60, 60), new("mapbosswarband1", 85, 85)], 11, _ => true, []);
        Assert.Equal((60, 60), (Assert.Single(withMapBoss).X, Assert.Single(withMapBoss).Y));
    }

    [Fact]
    public void Unassigned_map_boss_enters_disconnected_arena_via_local_transition_and_never_arrival()
    {
        // Main map (x < 120) walled off from a separate boss arena (x >= 140) reached via a local transition.
        bool ArenaWalk(int x, int y) => x is >= 0 and < 200 && y is >= 0 and < 200 && (x < 120 || x >= 140);
        var spawns = AreaPopulation.Plan(Area(1), 200, 200, ArenaWalk, (15, 15), [(155, 100)], [],
            42, _ => true, [], unassignedAtArrival: false,
            localTransitions: [((105, 100), (150, 100))]);
        var boss = Assert.Single(spawns);
        Assert.True(boss.X >= 140, $"expected boss inside arena (x >= 140), got ({boss.X}, {boss.Y})");
        Assert.True(ArenaWalk(boss.X, boss.Y));
    }
}

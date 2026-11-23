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
}

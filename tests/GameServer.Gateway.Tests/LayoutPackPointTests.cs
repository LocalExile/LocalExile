using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class LayoutPackPointTests
{
    static readonly PackCatalog.Pack Pack = new(0, "test-pack", 4, 4, 0, 0, [], [(5, 1)], new Dictionary<int, int>());

    static PackCatalog.AreaPacks Area(string id = "test") => new(id, 1, [(Pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);

    static List<AreaPopulation.Spawn> Around(IEnumerable<AreaPopulation.Spawn> spawns, AreaPopulation.RoomPoint p) =>
        [.. spawns.Where(s => Math.Abs(s.X - p.X) <= 9 && Math.Abs(s.Y - p.Y) <= 9)];

    // A placed room's "magic" and "rare" points become a magic pack and a pack with one rare, at the cells the room's
    // turn puts them (the placement the boss points already take, TutorialObjects.RoomPlacement); other points do not.
    [Fact]
    public void A_placed_rooms_pack_points_place_packs_of_their_rarity()
    {
        var guards = Pack with { Row = 7, Id = "test-guards", Entries = [(7, 1)] };
        var area = Area() with { DirectPacks = new Dictionary<string, PackCatalog.Pack> { [guards.Id] = guards } };
        var def = new RoomDef("Metadata/Terrain/Test/Rooms/side.arm", "", new TileKey { Width = 2, Height = 3 }, new RoomSlot[0, 0])
        {
            Points = [new(10, 20, 0, "magic"), new(30, 5, 0, "rare"), new(12, 12, 0, "mapboss"),
                new(40, 62, 0, guards.Id), new(15, 39, 0, guards.Id, 8), new(5, 45, 0, "foreign-guards")],
        };
        for (int code = 0; code < 8; code++)
        {
            var placed = new PlacedRoom(def, 4, 2, 2, 3, [], code);
            // A room is in every record that holds it: its points still count once.
            var record = new DoodadRecord(0, 0, 20, 20, new(), "", [placed]);
            var room = Assert.Single(AreaPopulation.PackRooms([record, record]));
            var at = new TutorialObjects.RoomPlacement(4 * 23, 2 * 23, 2 * 23, 3 * 23, code);
            Assert.Equal([at.Place(new("magic", 10, 20)) with { Angle = 0 }, at.Place(new("rare", 30, 5)) with { Angle = 0 }], room.Points);
            var direct = Assert.Single(AreaPopulation.DirectPackPoints([record, record], area));
            Assert.Equal(at.Place(new(guards.Id, 40, 62)) with { Angle = 0 }, direct);

            var spawns = AreaPopulation.Plan(area, 400, 400, (x, y) => x >= 0 && y >= 0 && x < 400 && y < 400, (390, 390), [], [],
                7, _ => true, [], roomPacks: [.. room.Points, direct]);
            Assert.Equal([MonsterRarity.Magic, MonsterRarity.Magic, MonsterRarity.Magic, MonsterRarity.Magic],
                Around(spawns, room.Points[0]).Select(s => s.Rarity));
            Assert.Equal([MonsterRarity.Rare, MonsterRarity.Normal, MonsterRarity.Normal, MonsterRarity.Normal],
                Around(spawns, room.Points[1]).Select(s => s.Rarity));
            Assert.Equal(4, Around(spawns, direct).Count(s => s.Pack == guards.Id && s.Rarity == MonsterRarity.Normal));
        }

        // A zero-weight area pack can stand without a scattered pool. Its expanded and full names may identify one site.
        var explicitOnly = area with { Scattered = [], Named = new Dictionary<string, PackCatalog.Pack> { ["guards"] = guards } };
        AreaPopulation.RoomPoint[] sites = [new(guards.Id, 40, 40), new(guards.Id, 80, 80), new(guards.Id, 80, 80)];
        List<AreaPopulation.Spawn> Place(bool ordinary) => AreaPopulation.Plan(explicitOnly, 120, 120, (_, _) => true, (10, 10),
            [], [new("%area%-guards", 40, 40)], 7, _ => true, [], roomPacks: sites, ordinaryPacks: ordinary);
        var placedGuards = Place(true);
        Assert.Equal(8, placedGuards.Count);
        Assert.Equal(4, Around(placedGuards, sites[0]).Count);
        Assert.Equal(4, Around(placedGuards, sites[1]).Count);
        Assert.Empty(Place(false));
    }

    // The Chamber of Innocence as the Gateway generates it: its rooms' twelve "magic" points all land on the floor, and
    // each has its magic pack. Needs the game files (data/game-files.zip).
    [GameFilesFact]
    public void A_generated_areas_pack_points_stand_on_its_floor()
    {
        const string area = "1_5_5";
        var level = ServerLevels.For(area, 2);
        var catalog = new PackCatalog();
        var packs = catalog.For(area);
        Assert.NotNull(level);
        Assert.NotNull(packs);
        bool Walk(int x, int y) => x >= 0 && y >= 0 && x < level.Width && y < level.Height && level.Walk[y * level.Width + x] >= 2;
        var points = level.PackRooms.SelectMany(r => r.Points).ToList();
        Assert.Equal(12, points.Count);
        Assert.All(points, p => Assert.True(p.Name == "magic" && Walk(p.X, p.Y), $"{p.Name} at ({p.X}, {p.Y})"));

        var entrance = level.Entrances[0];
        var spawns = AreaPopulation.Plan(packs, level.Width, level.Height, Walk, (entrance.X, entrance.Y), [], [], 7, _ => true, [],
            roomPacks: points);
        Assert.All(points, p => Assert.Contains(spawns, s => s.Rarity == MonsterRarity.Magic && s.X == p.X && s.Y == p.Y));

        var stronghold = catalog.For("1_4_4_3")!;
        var warriors = AreaPopulation.DirectPackPoints(ServerLevels.For("1_4_4_3", 1)!.Records, stronghold);
        Assert.Equal(32, warriors.Count(p => p.Name == "kaomwarriorpack"));
        Assert.Contains("pantheon-piety1", catalog.For("MapTier12_2Unique")!.DirectPacks.Keys);
        Assert.DoesNotContain("pantheon-piety1", catalog.For("Map2Tier14_2Unique")!.DirectPacks.Keys);
        Assert.Contains("1_2_7-bandits", catalog.For("1_2_7")!.DirectPacks.Keys);
        // The Lair map itself lists the first beast pack. Its shared waterfall room also names the sixth.
        var lair = catalog.For("MapWorldsLair")!.DirectPacks;
        Assert.Contains("chaos-beasts1", lair.Keys);
        Assert.DoesNotContain("chaos-beasts6", lair.Keys);
        var olmec = catalog.For("MapWorldsBoneCryptUnique")!.DirectPacks;
        Assert.Contains("legends-bosspack1", olmec.Keys);
        Assert.DoesNotContain("legends-bosspack3", olmec.Keys);
    }
}

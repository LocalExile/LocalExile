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
        var def = new RoomDef("Metadata/Terrain/Test/Rooms/side.arm", "", new TileKey { Width = 2, Height = 3 }, new RoomSlot[0, 0])
        {
            Points = [new GameServer.Data.Terrain.NamedPoint(10, 20, 0, "magic"), new GameServer.Data.Terrain.NamedPoint(30, 5, 0, "rare"), new GameServer.Data.Terrain.NamedPoint(12, 12, 0, "mapboss")],
        };
        for (int code = 0; code < 8; code++)
        {
            var placed = new PlacedRoom(def, 4, 2, 2, 3, [], code);
            // A room is in every record that holds it: its points still count once.
            var record = new DoodadRecord(0, 0, 20, 20, new(), "", [placed]);
            var room = Assert.Single(AreaPopulation.PackRooms([record, record]));
            var at = new TutorialObjects.RoomPlacement(4 * 23, 2 * 23, 2 * 23, 3 * 23, code);
            Assert.Equal([at.Place(new("magic", 10, 20)) with { Angle = 0 }, at.Place(new("rare", 30, 5)) with { Angle = 0 }], room.Points);

            var spawns = AreaPopulation.Plan(Area(), 400, 400, (x, y) => x >= 0 && y >= 0 && x < 400 && y < 400, (390, 390), [], [],
                7, _ => true, [], roomPacks: room.Points);
            Assert.Equal([MonsterRarity.Magic, MonsterRarity.Magic, MonsterRarity.Magic, MonsterRarity.Magic],
                Around(spawns, room.Points[0]).Select(s => s.Rarity));
            Assert.Equal([MonsterRarity.Rare, MonsterRarity.Normal, MonsterRarity.Normal, MonsterRarity.Normal],
                Around(spawns, room.Points[1]).Select(s => s.Rarity));
        }
    }

    // The Chamber of Innocence as the Gateway generates it: its rooms' twelve "magic" points all land on the floor, and
    // each has its magic pack. Needs the game files (data/game-files.zip).
    [Fact]
    public void A_generated_areas_pack_points_stand_on_its_floor()
    {
        const string area = "1_5_5";
        if (!File.Exists("data/game-files.zip") || ServerLevels.For(area, 2) is not { } level || new PackCatalog().For(area) is not { } packs) return;
        bool Walk(int x, int y) => x >= 0 && y >= 0 && x < level.Width && y < level.Height && level.Walk[y * level.Width + x] >= 2;
        var points = level.PackRooms.SelectMany(r => r.Points).ToList();
        Assert.Equal(12, points.Count);
        Assert.All(points, p => Assert.True(p.Name == "magic" && Walk(p.X, p.Y), $"{p.Name} at ({p.X}, {p.Y})"));

        var entrance = level.Entrances[0];
        var spawns = AreaPopulation.Plan(packs, level.Width, level.Height, Walk, (entrance.X, entrance.Y), [], [], 7, _ => true, [],
            roomPacks: points);
        Assert.All(points, p => Assert.Contains(spawns, s => s.Rarity == MonsterRarity.Magic && s.X == p.X && s.Y == p.Y));
    }
}

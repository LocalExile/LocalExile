using GameServer.Data.Terrain.Generation;

namespace GameServer.Data.Tests;

// The area transitions of a generated layout and where each leads, from the graph node of the room each stands in
// (AreaExits, Agents/docs/protocol/08-area-transitions.md §8). The rooms and points here are small stand-ins for the
// game's: each room is 4 × 4 tiles (92 × 92 cells), its named points in room cells.
public class AreaExitsTests
{
    const string Transition = "Metadata/MiscellaneousObjects/AreaTransition";

    // Room files by path: just the header, the room key and the named points (what RoomFile.Parse reads for points).
    readonly Dictionary<string, string> _rooms = [];

    TerrainCache Files() => new(path => _rooms.TryGetValue(path, out var text) ? System.Text.Encoding.UTF8.GetBytes(text) : null);

    PlacedRoom Room(string name, int tileX, int tileY, string[] descriptions, (int X, int Y, string Name)[] points, int orientation = 0)
    {
        string path = $"Metadata/Terrain/Test/{name}.arm";
        _rooms[path] = "version 36\n0\n\"test\"\nk 4 4 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n"
            + string.Concat(points.Select(p => $"{p.X} {p.Y} 0 \"{p.Name}\"\n")) + "-1\n";
        var key = new TileKey { Width = 4, Height = 4 };
        return new PlacedRoom(new RoomDef(path, "test", key, new RoomSlot[4, 4]), tileX, tileY, 4, 4, descriptions, orientation);
    }

    static PlacedTileObject At(int x, int y, string model = "exit.ao") => new(Transition, model, x, y, 0, float.NaN);

    List<PlacedExit> Resolve(PlacedRoom[] rooms, PlacedTileObject[] objects) => AreaExits.Resolve(AreaExits.Rooms(Files(), rooms), objects);

    [Fact]
    public void TheUpperPrisonsStairsAreAGateToTheWardensQuarters()
    {
        // _prisonupper_1_1.dgr "entranceup": "teleport1-BrutusAreaName" "telepad2" (its room's only point is "entrance");
        // Warden/_prisonboss3.dgr "entrancedown": "telepad1" "teleport2-%areaname%".
        var stairs = Room("transitionup", 0, 0, ["teleport1-BrutusAreaName", "telepad2"], [(40, 40, "entrance")]);
        var warden = Room("boss_transition", 10, 0, ["telepad1", "teleport2-%areaname%"], [(50, 50, "telepad")]);
        var exits = Resolve([stairs, warden], [At(45, 45), At(230 + 52, 52)]);
        Assert.Equal(ExitKind.Teleport, exits[0].Kind);
        Assert.Equal(1, exits[0].Number);
        Assert.Equal("BrutusAreaName", exits[0].Label);
        Assert.Equal((230 + 52, 52), exits[0].Target);   // beside the Warden's gate back
        Assert.Equal(ExitKind.Teleport, exits[1].Kind);
        Assert.Equal("%areaname%", exits[1].Label);
        Assert.Equal((45, 45), exits[1].Target);   // beside the stairs, the gate back
    }

    [Fact]
    public void ABossRoomTellsItsGateFromItsWayOnByItsPoints()
    {
        // Warden boss node: "telepad3" "teleport4-BrutusAreaName" "accessible" "entrance2" "AutoWaypoint"; the cells room
        // "telepad4" "teleport3disabled-BrutusArenaName" (starts locked in the game, the only way into the arena).
        var boss = Room("boss_exit", 0, 0, ["telepad3", "teleport4-BrutusAreaName", "accessible", "entrance2", "AutoWaypoint"],
            [(20, 20, "telepad"), (70, 70, "entrance")]);
        var cells = Room("boss_cells", 10, 0, ["telepad4", "teleport3disabled-BrutusArenaName"], [(30, 30, "telepad")]);
        var exits = Resolve([boss, cells], [At(72, 68), At(22, 18), At(230 + 33, 30)]);
        Assert.Equal((ExitKind.Entrance, 2, true), (exits[0].Kind, exits[0].Number, exits[0].Enabled));
        Assert.Equal((ExitKind.Teleport, 4), (exits[1].Kind, exits[1].Number));
        Assert.Equal((230 + 33, 30), exits[1].Target);
        Assert.Equal((ExitKind.Teleport, 3, false), (exits[2].Kind, exits[2].Number, exits[2].Enabled));
        Assert.Equal((22, 18), exits[2].Target);
    }

    [Fact]
    public void TheModelPlaysNoPart()
    {
        // The Cavern of Wrath's reskinned records and The Submerged Passage's depths entrance, which no static file lists,
        // take their room's exit all the same.
        var exits = Resolve([Room("transition_depths", 0, 0, ["entrance3"], [(40, 40, "entrance")])],
            [At(41, 41, "Metadata/Terrain/Cave/watery_depth_entrance_v01_01_area_transition.ao")]);
        Assert.Equal((ExitKind.Entrance, 3, true), (exits[0].Kind, exits[0].Number, exits[0].Enabled));
    }

    [Fact]
    public void AnExitTakesTheObjectNearestItsPoint()
    {
        // The Lower Prison's entrance room: a wall hole beside its "entrance" point and a ceiling marker far from it.
        var exits = Resolve([Room("transitiondown_low", 0, 0, ["entrance1"], [(10, 10, "entrance")])], [At(85, 85), At(14, 12)]);
        Assert.Equal(ExitKind.Unused, exits[0].Kind);
        Assert.Equal((ExitKind.Entrance, 1), (exits[1].Kind, exits[1].Number));
    }

    [Fact]
    public void AnEnabledEntranceChoosesBeforeADisabledOne()
    {
        // The Control Blocks' entrance room: "entrance2disabled" "entrance1", one generic "entrance" point.
        var exits = Resolve([Room("entrance_01", 0, 0, ["entrance2disabled", "entrance1"], [(60, 60, "entrance")])], [At(10, 10), At(62, 61)]);
        Assert.Equal((ExitKind.Entrance, 2, false), (exits[0].Kind, exits[0].Number, exits[0].Enabled));
        Assert.Equal((ExitKind.Entrance, 1, true), (exits[1].Kind, exits[1].Number, exits[1].Enabled));
    }

    [Fact]
    public void NumberedPointsHoldTheirOwnNumber()
    {
        // The Sarn Encampment's city_town.arm: "entrance1" … "entrance4" points.
        var exits = Resolve([Room("city_town", 0, 0, ["entrance2", "entrance1"], [(10, 10, "entrance1"), (80, 80, "entrance2")])],
            [At(12, 12), At(78, 78)]);
        Assert.Equal(1, exits[0].Number);
        Assert.Equal(2, exits[1].Number);
    }

    [Fact]
    public void GatesAndTelepadsShareTheRoomsPointsInOrderWithoutLoops()
    {
        // The Grand Promenade's garden2: "teleport2" "telepad1" "teleport1" "telepad2" at two "telepad" points: a stair
        // between two levels. The gate at point 0 is teleport2 and lands at point 1, and back.
        var garden = Room("garden2", 0, 0, ["teleport2", "telepad1", "teleport1", "telepad2"], [(10, 10, "telepad"), (80, 80, "telepad")]);
        var exits = Resolve([garden], [At(11, 11), At(79, 79)]);
        Assert.Equal((2, (79, 79)), (exits[0].Number, exits[0].Target));
        Assert.Equal((1, (11, 11)), (exits[1].Number, exits[1].Target));
        // The Weaver's treasure room lists both gates first: telepad1 and telepad2 take the points in order, and each gate
        // stands by the other's arrival (no gate lands on itself).
        var treasure = Room("treasure", 0, 0, ["teleport1", "teleport2", "telepad1", "telepad2"], [(10, 10, "telepad"), (80, 80, "telepad")]);
        exits = Resolve([treasure], [At(11, 11), At(79, 79)]);
        Assert.Equal((2, (79, 79)), (exits[0].Number, exits[0].Target));
        Assert.Equal((1, (11, 11)), (exits[1].Number, exits[1].Target));
    }

    [Fact]
    public void AnArrivalIsBesideTheGateBackToWhereThePlayerCameFrom()
    {
        // The Hall of Grandmasters: main.arm "teleport2" "teleport4" "telepad1" "telepad3" at "telepad" points that repeat;
        // each arena entrance has "teleportN" back and its "telepad". Arriving from an arena lands beside the door to it.
        var main = Room("main", 0, 0, ["default", "teleport2", "teleport4", "telepad1", "telepad3"], [(50, 50, "telepad"), (50, 50, "telepad")]);
        var a = Room("entrance_a", 10, 0, ["teleport1", "telepad2"], [(40, 40, "telepad")]);
        var b = Room("entrance_b", 20, 0, ["teleport3", "telepad4"], [(40, 40, "telepad")]);
        var exits = Resolve([main, a, b], [At(20, 20), At(70, 70), At(230 + 41, 41), At(460 + 41, 41)]);
        var toA = exits.Single(e => e.Number == 2);
        var toB = exits.Single(e => e.Number == 4);
        Assert.Equal((230 + 41, 41), toA.Target);
        Assert.Equal((460 + 41, 41), toB.Target);
        Assert.Equal((toA.Object.X, toA.Object.Y), exits.Single(e => e.Number == 1).Target);
        Assert.Equal((toB.Object.X, toB.Object.Y), exits.Single(e => e.Number == 3).Target);
        Assert.NotEqual(toA.Object, toB.Object);
    }

    [Fact]
    public void AnObjectNoPlayerCanReachTakesNoExitWhileAUsableOneIsLeft()
    {
        // The Harbour Bridge arena: its transition1 door lies next to the "telepad" point but 44 cells from walkable
        // ground; the gate back goes to the usable door.
        var arena = Room("bridge_arena", 0, 0, ["teleport1-%areaname%"], [(40, 40, "telepad")]);
        var exits = AreaExits.Resolve(AreaExits.Rooms(Files(), [arena]), [At(41, 41), At(60, 60)], usable: (x, y) => x > 50);
        Assert.Equal(ExitKind.Unused, exits[0].Kind);
        Assert.Equal(ExitKind.Teleport, exits[1].Kind);
    }

    [Fact]
    public void PointsTurnWithTheRoom()
    {
        // Orientation 2 (180°): room cell (10, 10) of a 92 × 92 room is (81, 81).
        var exits = Resolve([Room("turned", 0, 0, ["entrance1", "entrance2"], [(10, 10, "entrance1"), (80, 80, "entrance2")], orientation: 2)],
            [At(80, 80), At(12, 12)]);
        Assert.Equal(1, exits[0].Number);
        Assert.Equal(2, exits[1].Number);
    }

    [Fact]
    public void ObjectsOutsideAnExitRoomAreNotExits()
    {
        var exits = Resolve([Room("fill", 0, 0, ["shuffle1"], [(10, 10, "entrance")])], [At(20, 20), At(500, 500)]);
        Assert.All(exits, e => Assert.Equal(ExitKind.Unused, e.Kind));
    }
}

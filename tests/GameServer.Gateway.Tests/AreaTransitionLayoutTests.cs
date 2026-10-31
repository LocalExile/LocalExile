using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

// The exits of a layout the Gateway generates itself, from each object's graph node role (AreaExits;
// Agents/docs/protocol/08-area-transitions.md §8).
public class AreaTransitionLayoutTests
{
    const string Transition = "Metadata/MiscellaneousObjects/AreaTransition";

    static PlacedExit Exit(string model, int x, int y, ExitKind kind, int number = 0, bool enabled = true, (int, int)? target = null) =>
        new(new PlacedTileObject(Transition, model, x, y, 0, float.NaN), kind, number, enabled, null, target, "room.arm", "test");

    [Fact]
    public void Delves_mine_entrance_leads_into_the_mine_although_no_node_names_it()
    {
        // Delve_Hub's hub.arm names no exit; its foothills_DelveEntrance01 record is the way into the mine (as on a capture).
        var notes = new List<string>();
        var placed = AreaTransitionCatalog.FromExits("Delve_Hub", StashCatalog.DelveHub, [],
            [Exit("Metadata/Terrain/Mountain/Foothills/foothills_DelveEntrance01_area_transition.ao", 300, 200, ExitKind.Unused),
             Exit("Metadata/Terrain/Mountain/Foothills/foothills_elevator_area_transition.ao", 320, 200, ExitKind.Unused)], notes);
        var exit = Assert.Single(placed);
        Assert.Equal(InstanceDelve.MineArea, exit.DestinationHash16);
        Assert.Equal((300, 200), (exit.GridX, exit.GridY));
    }

    [Fact]
    public void Entrances_lead_to_their_connection_and_gates_within_the_area()
    {
        var notes = new List<string>();
        AreaTransitionCatalog.AtlasConnection[] connections = [new(1, "1_1_9", 0x1111), new(2, "1_1_11_2", 0x2222)];
        var placed = AreaTransitionCatalog.FromExits("1_1_11_1", 0x394e, connections,
            [Exit("a.ao", 10, 10, ExitKind.Entrance, 2), Exit("b.ao", 20, 20, ExitKind.Teleport, 1, target: (90, 90)),
             Exit("c.ao", 30, 30, ExitKind.Entrance, 1, enabled: false), Exit("d.ao", 40, 40, ExitKind.Entrance, 3)], notes);
        Assert.Equal(2, placed.Count);
        Assert.Equal(0x2222, placed[0].DestinationHash16);
        Assert.Null(placed[0].TeleportTo);
        Assert.Equal(0x394e, placed[1].DestinationHash16);
        Assert.Equal((90, 90), placed[1].TeleportTo);
        Assert.Contains(notes, n => n.Contains("entrance3") && n.Contains("not created"));
    }

    [Fact]
    public void An_entrance_without_an_object_of_its_own_is_an_exit_at_its_point()
    {
        // Karui Shores' camp.arm "entrance1" to Oriath and The Harvest's boss_2 "entrance2" to Highgate (the game's portal
        // after Malachai; the room's only object is the arena gate): exits without a model at the entrance point. Not for
        // a disabled entrance, one that has its object, or a destination another exit already reaches.
        var notes = new List<string>();
        AreaTransitionCatalog.AtlasConnection[] connections = [new(1, "a", 0x3333), new(2, "b", 0x4444), new(3, "c", 0x5555), new(4, "a", 0x3333)];
        var placed = AreaTransitionCatalog.FromExits("x", 0x6666, connections,
            [Exit("gate.ao", 50, 50, ExitKind.Teleport, 1, target: (90, 90)), Exit("door.ao", 20, 20, ExitKind.Entrance, 3)], notes,
            [new EntrancePoint(1, true, 563, 157), new EntrancePoint(2, false, 10, 10), new EntrancePoint(3, true, 21, 21),
             new EntrancePoint(4, true, 30, 30)]);
        Assert.Equal(3, placed.Count);
        var point = placed[2];
        Assert.Equal((563, 157, (ushort)0x3333), (point.GridX, point.GridY, point.DestinationHash16));
        Assert.Null(point.Model);
    }
}

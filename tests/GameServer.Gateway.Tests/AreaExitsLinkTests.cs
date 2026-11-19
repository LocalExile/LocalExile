using GameServer.Data.Terrain.Generation;
using Xunit;

namespace GameServer.Gateway.Tests;

// An ExtraTerrainFeatures row whose descriptions say "link" (HarvestEncounter) places a room in the map and adds its world
// area as a sub-area record whose node says "link" (night_garden.dgr's garden): AreaExits pairs them into a gate each way.
public class AreaExitsLinkTests
{
    const string Transition = "Metadata/MiscellaneousObjects/AreaTransition";

    readonly Dictionary<string, string> _rooms = [];

    TerrainCache Files() => new(path => _rooms.TryGetValue(path, out var text) ? System.Text.Encoding.UTF8.GetBytes(text) : null);

    // A 4 × 4 room (92 × 92 cells) at tile (tileX, 0) with one "telepad" point.
    PlacedRoom Room(string name, int tileX, string[] descriptions, (int X, int Y) telepad)
    {
        string path = $"Metadata/Terrain/Test/{name}.arm";
        _rooms[path] = $"version 36\n0\n\"test\"\nk 4 4 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n{telepad.X} {telepad.Y} 0 \"telepad\"\n-1\n";
        return new PlacedRoom(new RoomDef(path, "test", new TileKey { Width = 4, Height = 4 }, new RoomSlot[4, 4]), tileX, 0, 4, 4, descriptions);
    }

    static DoodadRecord Record(params PlacedRoom[] rooms) => new(0, 0, 40, 40, [], "test.tsi", rooms);

    static PlacedTileObject At(int x, int y) => new(Transition, "portal.ao", x, y, 0, float.NaN);

    [Fact]
    public void AFeatureRoomAndItsSubAreaLinkRoomBecomeGatesToEachOther()
    {
        var encounter = Room("harvest_encounter", 0, [], (30, 27)) with { Feature = "X", FeatureDescriptions = ["link"] };
        var lonely = Room("harvest_encounter2", 20, [], (30, 27)) with { Feature = "X", FeatureDescriptions = ["link"] };
        var garden = Room("garden", 10, ["default", "link"], (50, 50));
        var records = new[] { Record(encounter, lonely), Record(garden) with { FeatureRow = "X", FeatureWorldArea = 1333 } };
        var objects = new[] { At(34, 37), At(230 + 52, 52), At(460 + 34, 37) };

        var (exits, _) = AreaExits.Resolve(Files(), records, objects);

        Assert.Equal(ExitKind.Teleport, exits[0].Kind);
        Assert.Equal(ExitKind.Teleport, exits[1].Kind);
        Assert.Equal("X", exits[0].Feature);
        // The map-side gate names the sub-area's world area (its label); the grove-side gate keeps the map's own.
        Assert.Equal(1333, exits[0].FeatureWorldArea);
        Assert.Equal(-1, exits[1].FeatureWorldArea);
        Assert.True(exits[0].Target is { X: >= 230 and < 322, Y: >= 0 and < 92 }, $"map gate lands at {exits[0].Target}");
        Assert.True(exits[1].Target is { X: >= 0 and < 92, Y: >= 0 and < 92 }, $"grove gate lands at {exits[1].Target}");
        Assert.Equal(ExitKind.Unused, exits[2].Kind);
        Assert.Contains("link without a partner", exits[2].Why);
    }
}

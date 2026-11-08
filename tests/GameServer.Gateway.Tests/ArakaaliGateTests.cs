using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class ArakaaliGateTests
{
    [Fact]
    public void ArenaEntryUsesThePlacedRoomFloorAndPreservesOtherGateDestinations()
    {
        foreach (var (x, y, w, h, orientation) in new[] { (33, 17, 13, 17, 0), (10, 20, 17, 13, 1), (21, 8, 13, 17, 4) })
        {
            var room = new PlacedRoom(new RoomDef("Metadata/Terrain/Act7/Area12Level2/Rooms/boss.arm", "", new TileKey(), new RoomSlot[0, 0]),
                x, y, w, h, [], orientation);
            var level = new ServerLevels.Level(2000, 2000, [], [], [], [], [], [], [])
                { Records = [new DoodadRecord(0, 0, 0, 0, new(), "", [room])] };
            var layout = new AreaLayout(1, "2_7_12_2", 2000, 2000, 0, 0, (_, _) => true, []) { Level = level };
            var pair = (X: x * 23 + 40, Y: y * 23 + 30);
            var entry = new AreaTransitionObject(0, 0, 1)
                { TeleportTo = pair, SourceType = "Metadata/QuestObjects/BossArenaEntranceTransition" };
            var centre = (X: x * 23 + w * 23 / 2, Y: y * 23 + h * 23 / 2);
            Assert.Equal(centre, Game.GateArrival(layout, entry));
            Assert.Equal(layout.Near(pair), Game.GateArrival(layout, entry with { SourceType = AreaTransitionObject.MetadataPath }));
            Assert.Equal(layout.Near(pair), Game.GateArrival(layout with { Id = "other" }, entry));
            var obstructed = layout with { Walkable = (px, py) => (px, py) != centre };
            var adjusted = Game.GateArrival(obstructed, entry);
            Assert.NotEqual(centre, adjusted);
            Assert.True(obstructed.WalkableFor(adjusted.X, adjusted.Y, 1));
            Assert.InRange(adjusted.X, x * 23, (x + w) * 23 - 1);
            Assert.InRange(adjusted.Y, y * 23, (y + h) * 23 - 1);
        }
    }
}

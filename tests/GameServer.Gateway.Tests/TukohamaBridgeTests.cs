using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public class TukohamaBridgeTests
{
    [Fact]
    public void Both_keep_entrances_and_both_exits_cross_their_own_bridge()
    {
        // Door order here is the client's tile order, not the .arm point order.
        const uint keepLabel = 3454977763;
        foreach (bool reflected in new[] { false, true })
        {
            (int X, int Y) Place(int x, int y) => (782 + (reflected ? 23 * 20 - 1 - x : x), 161 + y);
            var def = new RoomDef("Metadata/Terrain/Act6/Area5/Rooms/miniboss.arm", "miniboss", new TileKey { Width = 20, Height = 30 }, new RoomSlot[20, 30])
            {
                Points = [new(242, 325, 0, "telepad"), new(245, 145, 0, "telepad"), new(260, 474, 0, "telepad"), new(263, 627, 0, "telepad")]
            };
            var room = new PlacedRoom(def, 34, 7, 20, 30, [], reflected ? 4 : 0);
            var level = new ServerLevels.Level(2000, 2000, [], [], [], [], [], [], [])
                { Records = [new DoodadRecord(0, 0, 0, 0, new(), "", [room])] };
            AreaTransitionObject Door(int x, int y, int side, uint? label)
            {
                var (gx, gy) = Place(x, y);
                return new(gx, gy, 1, $"Metadata/Terrain/Beach/Features/swamp_longbridge_v01_01_area_transition{side}.ao")
                    { TeleportTo = Place(242, 316), Label = label };
            }
            var other = new AreaTransitionObject(12, 24, 2);
            var layout = new AreaLayout(1, "2_6_5", 2000, 2000, 0, 0, (_, _) => true,
                [(11, Door(242, 316, 1, null)), (12, Door(240, 189, 2, keepLabel)),
                 (13, Door(265, 615, 1, null)), (14, Door(263, 488, 2, keepLabel)), (15, other)]) { Level = level };
            var client = new ClientState(layout, 1, 1);
            Game.PrepareKaruiBridgeGates(client);
            var gates = client.Layout.AreaTransitions.ToDictionary(g => g.Id, g => g.Transition);
            Assert.Equal(Place(245, 145), gates[11].TeleportTo);
            Assert.Equal(Place(242, 325), gates[12].TeleportTo);
            Assert.Equal(Place(260, 474), gates[13].TeleportTo);
            Assert.Equal(Place(263, 627), gates[14].TeleportTo);
            Assert.Null(gates[11].Label); Assert.Equal(keepLabel, gates[12].Label);
            Assert.Equal(keepLabel, gates[13].Label); Assert.Null(gates[14].Label);
            Assert.Same(other, gates[15]);
        }
    }
}

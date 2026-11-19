namespace GameServer.Gateway.Tests;

public sealed class MapDeviceRebindTests
{
    // A hideout's instance is gone six minutes after its last player leaves, and the device and the portals standing at
    // it go with it (live 2026-11-18: a disconnect inside the Simulacrum came back to a device with no portals). The run
    // moves to the instance of the same area the character enters, so the portals left stand at the new device.
    [Fact]
    public void A_run_whose_device_instance_expired_moves_to_the_instance_entered()
    {
        const string owner = "map-rebind-owner";
        var area = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        var hideout = new MapDevice.Origin(0x872c, 1, 563, 558);
        try
        {
            var run = MapDevice.Activate(owner, area, returnTo: hideout, entrance: hideout);
            MapDevice.Entered(run, 987702);
            Assert.NotNull(MapDevice.TakePortal(owner, 0, run.Run));
            Assert.Equal(987702UL, MapDevice.AtEntrance(hideout.Area, hideout.Instance)!.Instance);

            // The map's instance keeps the way back it was entered with, for its own return portal.
            var inMap = new InstanceState();
            Assert.Equal(1UL, MapDevice.ForInstance(area.Hash16, 987702, inMap)!.ReturnTo!.Instance);

            // The character enters a new instance of the same area: the entrance and the way back follow it.
            var moved = MapDevice.Rebind(owner, hideout.Area, 3)!;
            Assert.Equal(3UL, moved.Entrance!.Instance);
            Assert.Equal(3UL, moved.ReturnTo!.Instance);
            Assert.Null(MapDevice.AtEntrance(hideout.Area, hideout.Instance));
            var atNew = MapDevice.AtEntrance(hideout.Area, 3)!;
            Assert.Equal((987702UL, 5), (atNew.Instance, atNew.PortalsLeft));
            // So does the map's kept copy: the portal back inside the map leads to the new device, not to a dead instance.
            var kept = MapDevice.ForInstance(area.Hash16, 987702, inMap)!;
            Assert.Equal((987702UL, 3UL), (kept.Instance, kept.ReturnTo!.Instance));

            // Another area is not the device's: nothing moves.
            Assert.Same(moved, MapDevice.Rebind(owner, 0x1234, 4));
        }
        finally
        {
            MapDevice.Close(owner);
        }
    }
}

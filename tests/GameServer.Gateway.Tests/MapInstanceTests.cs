namespace GameServer.Gateway.Tests;

public sealed class MapInstanceTests
{
    [Fact]
    public void A_map_instance_keeps_the_map_it_was_opened_with()
    {
        var strand = new MapDevice.MapArea(0x1234, "MapWorldsStrand", "Strand Map", 3) { Level = 70 };
        var first = new MapMods(new Dictionary<string, int> { ["map_monsters_life_+%"] = 40 });
        var second = new MapMods(new Dictionary<string, int> { ["map_monsters_damage_+%"] = 25 });
        var store = new InstanceStore(TimeProvider.System, TimeSpan.FromMinutes(8));
        const string owner = "MapInstanceRegressionOwner";
        try
        {
            // The first portal makes the instance: the transfer carries the map, Auth answers with instance 41.
            var open = MapDevice.Activate(owner, strand, first);
            var (taken, _) = MapDevice.TakePortal(owner, 0)!.Value;
            MapDevice.Arrivals[0xa1] = MapDevice.Run.Of(owner, taken);
            MapDevice.Entered(owner, strand, 41);
            var instance = store.Enter(41, 1);
            Assert.Same(first, MapDevice.Admit(instance, 0xa1, strand.Hash16, 41)!.Mods);
            Assert.False(MapDevice.Arrivals.ContainsKey(0xa1));

            // The owner opens another map of the same area: instance 41 still plays the first one, for a login inside it
            // and for a guest who joins it.
            MapDevice.Activate(owner, strand, second);
            Assert.Same(first, MapDevice.Admit(store.Enter(41, 1), 0xa2, strand.Hash16, 41)!.Mods);
            Assert.Equal(owner, instance.Map!.Owner);
            Assert.Equal(70, instance.Map.Area.Level);

            // Its last portal closes the device, but the player it takes in still gets the map, and so does a guest after.
            MapDevice.Entered(owner, strand, 42);
            MapDevice.Run? last = null;
            for (int i = 0; i < MapDevice.PortalCount; i++)
                if (MapDevice.TakePortal(owner, i) is var (map, _)) last = MapDevice.Run.Of(owner, map);
            Assert.Null(MapDevice.OpenOf(owner));
            MapDevice.Arrivals[0xa3] = last!;
            var later = store.Enter(42, 1);
            Assert.Same(second, MapDevice.Admit(later, 0xa3, strand.Hash16, 42)!.Mods);
            Assert.Same(second, MapDevice.Admit(store.Enter(42, 1), 0xa4, strand.Hash16, 42)!.Mods);

            // An area the map does not open plays without it.
            Assert.Null(MapDevice.Admit(store.Enter(43, 1), 0xa5, 0x4321, 43));
        }
        finally { MapDevice.Close(owner); }
    }
}

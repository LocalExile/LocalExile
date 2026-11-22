namespace GameServer.Gateway.Tests;

public sealed class MapInstanceTests
{
    [Fact]
    public void A_map_instance_keeps_the_map_it_was_opened_with()
    {
        var strand = new MapDevice.MapArea(0x1234, "MapWorldsStrand", "Strand Map", 3) { Level = 70 };
        var first = new MapMods(new Dictionary<uint, int> { [Stat.MapMonstersLifePlusPct] = 40 });
        var second = new MapMods(new Dictionary<uint, int> { [Stat.MapMonstersDamagePlusPct] = 25 });
        var store = new InstanceStore(TimeProvider.System, TimeSpan.FromMinutes(8));
        const string owner = "MapInstanceRegressionOwner";
        try
        {
            // The first portal makes the instance: the transfer carries the map, Auth answers with instance 41.
            var open = MapDevice.Activate(owner, strand, first);
            var (taken, _) = MapDevice.TakePortal(owner, 0)!.Value;
            MapDevice.TicketArrivals[0xa1] = MapDevice.Run.Of(owner, taken);
            MapDevice.Entered(owner, strand, 41);
            var instance = store.Enter(41, 1);
            Assert.Same(first, MapDevice.Admit(instance, 0xa1, strand.Hash16, 41)!.Mods);
            Assert.False(MapDevice.TicketArrivals.ContainsKey(0xa1));

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
            Assert.Equal(0, MapDevice.OpenOf(owner)!.PortalsLeft); // Retained for completion and return routing.
            MapDevice.TicketArrivals[0xa3] = last!;
            var later = store.Enter(42, 1);
            Assert.Same(second, MapDevice.Admit(later, 0xa3, strand.Hash16, 42)!.Mods);
            Assert.Same(second, MapDevice.Admit(store.Enter(42, 1), 0xa4, strand.Hash16, 42)!.Mods);

            // An area the map does not open plays without it.
            Assert.Null(MapDevice.Admit(store.Enter(43, 1), 0xa5, 0x4321, 43));

            // The area stats are composed by the instance's first connection only: a later one (another atlas, another
            // map) gets the same list, so its level is the one already generated.
            int composed = 0;
            var mutable = new Dictionary<uint, int> { [1] = 40 };
            var stats = instance.AreaStats(() => { composed++; return mutable; });
            mutable[1] = 999;
            Assert.Equal(40, stats[1]);
            var again = instance.AreaStats(() => { composed++; return new Dictionary<uint, int> { [1] = 99 }; });
            Assert.Same(stats, again);
            Assert.Equal(1, composed);
            Assert.Equal(new Dictionary<uint, int> { [1] = 40, [2] = 5 },
                InstanceEvents.Merge(stats, new Dictionary<uint, int> { [2] = 5 }));
            var areaRule = new Dictionary<uint, int> { [1] = 7 };
            uint seedA = ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70, areaRule);
            areaRule[1] = 11;
            uint seedB = ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70, areaRule);
            Assert.NotEqual(seedA, seedB);
            Assert.Equal(seedA, ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70,
                new Dictionary<uint, int> { [1] = 7 }));
            Assert.Equal(seedB, ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70, areaRule));

            uint life = Stat.MapMonstersLifePlusPct;
            uint effect = Stat.MapModEffectPlusPctForNonUniqueMaps;
            var explicitStats = new Dictionary<uint, int> { [Stat.MapMonstersLifePlusPct] = 40 };
            var atlas = new Dictionary<uint, int> { [effect] = 50 };
            var unique = new MapMods(explicitStats) { MapRarity = GameServer.Core.Protocol.Instance.ItemRarity.Unique,
                ExplicitMods = [explicitStats] };
            Assert.Equal(40, Game.ComposeMapAreaStats(unique, 3, atlas, new HashSet<string>(), 0)[life]);
            var rare = new MapMods(explicitStats) { ExplicitMods = [explicitStats] };
            Assert.Equal(60, Game.ComposeMapAreaStats(rare, 3, atlas, new HashSet<string>(), 0)[life]);
            Assert.Equal(40, Game.ComposeMapAreaStats(first, 3, atlas, new HashSet<string>(), 0)[life]);
        }
        finally { MapDevice.Close(owner); }
    }
}

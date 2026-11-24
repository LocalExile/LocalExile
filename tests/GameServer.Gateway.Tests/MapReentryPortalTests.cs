namespace GameServer.Gateway.Tests;

public sealed class MapReentryPortalTests
{
    // Issue #1002: a town portal opened in a map leads to the hideout and leaves no portal back beside the device, so an
    // entry through the device's portals (after a death too) arrived at the map's start instead of at that portal.
    [Fact]
    public async Task An_entry_through_the_map_device_arrives_at_the_players_own_portal_in_that_map()
    {
        const string owner = "map-reentry-owner", guest = "map-reentry-guest";
        var area = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        var device = new MapDevice.Origin(0x1234, 7, 50, 50);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        Player Player(InstanceState instance, ulong number) =>
            new(new(area.Hash16, area.Id, 300, 300, 10, 10, null, []), 0, 1)
            {
                Character = { Name = owner, Account = owner },
                Instance = instance,
                InstanceNumber = number,
                Connection = { PlayerSent = true, Session = new(owner, wire.Stream, wire.Ciphers) },
                PlayerLife = 100
            };
        try
        {
            var run = MapDevice.Activate(owner, area, entrance: device);
            Assert.Null(PortalsFeature.MapEntrySpot(owner, run));   // the first entry makes the instance: its start
            MapDevice.Entered(run, 987700);
            var entered = MapDevice.OpenOf(owner)!;
            var instance = new InstanceState();
            var inMap = Player(instance, 987700);
            await PortalsFeature.OpenTownPortal(inMap, 120, 140, "test");
            var portal = (inMap.ReturnPortal!.Value.X, inMap.ReturnPortal!.Value.Y);
            // Still no extra entrance beside the device: re-entry spends the shared portals, and arrives at this one.
            Assert.False(PortalsRules.OpenPortals.ContainsKey(owner));
            Assert.Equal(portal, PortalsFeature.MapEntrySpot(owner, entered));
            Assert.Equal(portal, PortalsFeature.MapEntrySpot(guest, entered));   // party entries through the device arrive at the active portal in that map

            // Resurrect at Checkpoint in the map leaves it for the device it was entered from, as the official servers do.
            var exit = Game.MapCheckpointExit(inMap)!;
            Assert.Equal((device.Area, device.Instance, (int?)device.X, (int?)device.Y),
                (exit.Area, exit.Instance, exit.ArriveAt?.X, exit.ArriveAt?.Y));

            // Back in that instance, the portal stands at its cell again, in place of the map's own portal back: one
            // portal per player, none by the map's start.
            var back = Player(instance, 987700);
            await PortalsFeature.SpawnMapReturnPortal(back);
            Assert.Null(back.ReturnPortal);
            await PortalsFeature.SpawnTownPortals(back);
            Assert.Equal(portal, (back.ReturnPortal!.Value.X, back.ReturnPortal!.Value.Y));

            // A new run of the map is another instance: its entries start at the map's start.
            var replacement = MapDevice.Activate(owner, area, entrance: device);
            MapDevice.Entered(replacement, 987701);
            Assert.Null(PortalsFeature.MapEntrySpot(owner, MapDevice.OpenOf(owner)!));

            // Outside maps the checkpoint is in the area.
            var coast = new Player(new(0x7ad2, "1_1_2", 300, 300, 10, 10, null, []), 0, 1)
                { Character = { Name = owner }, Instance = new InstanceState(), InstanceNumber = 5 };
            Assert.Null(Game.MapCheckpointExit(coast));
        }
        finally
        {
            MapDevice.Close(owner);
            PortalsRules.MapTownPortals.TryRemove(owner, out _);
            PortalsRules.OpenPortals.TryRemove(owner, out _);
        }
    }

    // The client plays a portal's opening the first time 0x8f lists it (21-hideouts.md §5): a portal standing again on
    // entry is listed before its create, so it shows idle; one opened now is listed only after it, so it opens.
    [Fact]
    public async Task A_portal_standing_on_entry_is_listed_before_its_create_and_a_new_one_after()
    {
        const string owner = "map-standing-portal";
        var area = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        Player Player(InstanceState instance) =>
            new(new(area.Hash16, area.Id, 300, 300, 10, 10, null, []), 0, 1)
            {
                Character = { Name = owner, Account = owner },
                Connection = { PlayerSent = true, Session = new(owner, wire.Stream, wire.Ciphers) },
                Instance = instance, InstanceNumber = 987710, PlayerLife = 100,
            };
        // Where the 0x008f listing this portal and its 0x02bb stand in what was sent.
        (int List, int Create) Order(uint id)
        {
            var lines = Program.HistoryFor(wire.Ciphers).Lines();
            string hex = id.ToString("X8");
            return (lines.ToList().FindIndex(l => l.Contains(" 0x008f ") && l.Contains("01" + hex)),
                lines.ToList().FindIndex(l => l.Contains(" 0x02bb ") && l.Contains("02BB" + hex)));
        }
        try
        {
            MapDevice.Entered(MapDevice.Activate(owner, area, entrance: new(0x1234, 7, 50, 50)), 987710);
            var instance = new InstanceState();
            var opening = Player(instance);
            await PortalsFeature.OpenTownPortal(opening, 120, 140, "test");
            var fresh = Order(opening.ReturnPortal!.Value.Id);
            Assert.True(fresh.Create >= 0 && (fresh.List < 0 || fresh.List > fresh.Create));

            var entering = Player(instance);
            await PortalsFeature.SpawnTownPortals(entering);
            var standing = Order(entering.ReturnPortal!.Value.Id);
            Assert.True(standing.List >= 0 && standing.List < standing.Create);
        }
        finally
        {
            MapDevice.Close(owner);
            PortalsRules.MapTownPortals.TryRemove(owner, out _);
            PortalsRules.OpenPortals.TryRemove(owner, out _);
        }
    }

    [Fact]
    public async Task A_players_open_portal_is_visible_to_a_peer_in_the_same_instance()
    {
        const string owner = "peer-portal-owner", guest = "peer-portal-guest", late = "peer-portal-late";
        var area = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        using var runtime = new PartyFollowerTests.Runtime();
        using var ownerWire = new PartyFollowerTests.Wire();
        using var guestWire = new PartyFollowerTests.Wire();
        using var lateWire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        Player Make(string name, PartyFollowerTests.Wire wire)
        {
            var c = new Player(new(area.Hash16, area.Id, 300, 300, 10, 10, null, []), 0, 1)
            {
                Character = { Name = name, Account = name },
                Connection = { PlayerSent = true, Session = new(name, wire.Stream, wire.Ciphers) },
                Instance = instance, InstanceNumber = 987720, PlayerLife = 100,
            };
            Program.InGame.TryAdd(c, 0);
            instance.AddPlayer(c);
            return c;
        }
        var ownerConn = Make(owner, ownerWire);
        var guestConn = Make(guest, guestWire);
        try
        {
            MapDevice.Entered(MapDevice.Activate(owner, area, entrance: new(0x1234, 7, 50, 50)), 987720);
            await PortalsFeature.OpenTownPortal(ownerConn, 120, 140, "test");

            Assert.Single(guestConn.PeerPortals);
            var peerPortal = Assert.Single(guestConn.PeerPortals.Values);
            Assert.Equal(owner, peerPortal.Owner);
            Assert.Equal(ownerConn.ReturnPortal!.Value.X, peerPortal.X);
            Assert.Equal(ownerConn.ReturnPortal!.Value.Y, peerPortal.Y);

            var lateConn = Make(late, lateWire);
            try
            {
                await PortalsFeature.SpawnMapReturnPortal(lateConn);
                Assert.Null(lateConn.ReturnPortal);
                await PortalsFeature.SpawnTownPortals(lateConn);
                var latePeerPortal = Assert.Single(lateConn.PeerPortals);
                Assert.Equal(owner, latePeerPortal.Value.Owner);
                var lines = Program.HistoryFor(lateWire.Ciphers).Lines().ToList();
                string hex = latePeerPortal.Key.ToString("X8");
                int listed = lines.FindIndex(l => l.Contains(" 0x008f ") && l.Contains("01" + hex));
                int created = lines.FindIndex(l => l.Contains(" 0x02bb ") && l.Contains("02BB" + hex));
                Assert.True(listed >= 0 && created >= 0 && listed < created);
            }
            finally { lateConn.Closed = true; Program.InGame.TryRemove(lateConn, out _); }
        }
        finally
        {
            ownerConn.Closed = true; guestConn.Closed = true;
            Program.InGame.TryRemove(ownerConn, out _);
            Program.InGame.TryRemove(guestConn, out _);
            MapDevice.Close(owner);
            PortalsRules.MapTownPortals.TryRemove(owner, out _);
            PortalsRules.OpenPortals.TryRemove(owner, out _);
        }
    }
}

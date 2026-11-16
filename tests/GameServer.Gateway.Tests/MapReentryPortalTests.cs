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
        ClientState Player(InstanceState instance, ulong number) =>
            new(new(area.Hash16, area.Id, 300, 300, 10, 10, null, []), 0, 1)
            {
                Character = owner, Account = owner, Instance = instance, InstanceNumber = number, PlayerSent = true,
                PlayerLife = 100, Wire = (owner, wire.Stream, wire.Ciphers),
            };
        try
        {
            var run = MapDevice.Activate(owner, area, entrance: device);
            Assert.Null(PortalsFeature.MapEntrySpot(owner, run));   // the first entry makes the instance: its start
            MapDevice.Entered(run, 987700);
            var entered = MapDevice.OpenOf(owner)!;
            var instance = new InstanceState();
            var inMap = Player(instance, 987700);
            await PortalsFeature.OpenTownPortal("map-reentry", wire.Stream, wire.Ciphers, inMap, 120, 140, "test");
            var portal = (inMap.ReturnPortal!.Value.X, inMap.ReturnPortal!.Value.Y);
            // Still no extra entrance beside the device: re-entry spends the shared portals, and arrives at this one.
            Assert.False(PortalsRules.OpenPortals.ContainsKey(owner));
            Assert.Equal(portal, PortalsFeature.MapEntrySpot(owner, entered));
            Assert.Null(PortalsFeature.MapEntrySpot(guest, entered));   // each player's entry goes to their own portal

            // Resurrect at Checkpoint in the map leaves it for the device it was entered from, as the official servers do.
            var exit = Game.MapCheckpointExit(inMap)!;
            Assert.Equal((device.Area, device.Instance, (int?)device.X, (int?)device.Y),
                (exit.Area, exit.Instance, exit.ArriveAt?.X, exit.ArriveAt?.Y));

            // Back in that instance, the portal stands at its cell again, in place of the map's own portal back: one
            // portal per player, none by the map's start.
            var back = Player(instance, 987700);
            await PortalsFeature.SpawnMapReturnPortal("map-reentry-back", wire.Stream, wire.Ciphers, back);
            Assert.Null(back.ReturnPortal);
            await PortalsFeature.SpawnTownPortals("map-reentry-back", wire.Stream, wire.Ciphers, back);
            Assert.Equal(portal, (back.ReturnPortal!.Value.X, back.ReturnPortal!.Value.Y));

            // A new run of the map is another instance: its entries start at the map's start.
            var replacement = MapDevice.Activate(owner, area, entrance: device);
            MapDevice.Entered(replacement, 987701);
            Assert.Null(PortalsFeature.MapEntrySpot(owner, MapDevice.OpenOf(owner)!));

            // Outside maps the checkpoint is in the area.
            var coast = new ClientState(new(0x7ad2, "1_1_2", 300, 300, 10, 10, null, []), 0, 1)
                { Character = owner, Instance = new InstanceState(), InstanceNumber = 5 };
            Assert.Null(Game.MapCheckpointExit(coast));
        }
        finally
        {
            MapDevice.Close(owner);
            PortalsRules.MapTownPortals.TryRemove(owner, out _);
            PortalsRules.OpenPortals.TryRemove(owner, out _);
        }
    }
}

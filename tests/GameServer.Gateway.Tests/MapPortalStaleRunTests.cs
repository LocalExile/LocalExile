namespace GameServer.Gateway.Tests;

public sealed class MapPortalStaleRunTests
{
    // Review on #1109: a refused transfer's Done is fire-and-forget, so EndMapRun for run A can run after the device
    // was activated again (run B). It must not remove B's six portals nor stop B's placement sequence.
    [Fact]
    public async Task A_late_failure_of_an_older_run_leaves_the_newer_runs_portals_alone()
    {
        const string owner = "stale-run-owner";
        var area = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        var entrance = new MapDevice.Origin(0x1234, 1, 50, 50);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var player = runtime.PlayerOf(new InstanceState(), wire, owner);
        try
        {
            var first = MapDevice.Activate(owner, area, entrance: entrance);
            var second = MapDevice.Activate(owner, area, entrance: entrance);
            // The connection shows the newer run's six portals, its sequence placing them.
            player.MapPortalRun = second;
            for (uint id = 1; id <= 6; id++) player.MapPortals[id] = (50 + (int)id, 50, (int)id - 1);
            int generation = player.MapPortalGeneration;

            // The refused transfer of the older run lands now.
            await PortalsFeature.EndMapRun(player, first, "the map portal did not open");

            Assert.Equal(6, player.MapPortals.Count);
            Assert.Equal(generation, player.MapPortalGeneration);
            Assert.Equal(second.Run, MapDevice.OpenOf(owner)!.Run);
        }
        finally
        {
            MapDevice.Close(owner);
        }
    }
}

namespace GameServer.Gateway.Tests;

public sealed class MapReturnPortalExpiryTests
{
    // A hideout's instance is gone six minutes after its last player leaves, and a map's portal back and a Portal Scroll's
    // portal both led into the instance the device stood in: Auth refused the transfer ("instance N unavailable in this
    // league") and the player stayed in the map (live 2026-11-18, the Simulacrum).
    [Fact]
    public void A_return_portal_enters_the_live_instance_and_a_new_one_of_the_area_when_it_expired()
    {
        var device = new MapDevice.Origin(0x872c, 1, 563, 228);

        var live = PortalsFeature.ReturnTransfer(device, instanceGone: false);
        Assert.Equal((device.Area, device.Instance, false), (live.Area, live.Instance, live.New));
        Assert.Equal((device.X, device.Y), (live.ArriveAt!.Value.X, live.ArriveAt!.Value.Y));
        Assert.False(live.AtWaypoint);

        var gone = PortalsFeature.ReturnTransfer(device, instanceGone: true);
        Assert.Equal((device.Area, 0ul, false), (gone.Area, gone.Instance, gone.New));
        Assert.Null(gone.ArriveAt);
        Assert.True(gone.AtWaypoint);
    }

    [Fact]
    public void Only_a_town_or_a_hideout_is_recreated_on_entry()
    {
        Assert.True(PortalsFeature.RecreatedOnEntry(AreaLayouts.LioneyesWatch));
        Assert.True(PortalsFeature.RecreatedOnEntry(HideoutCatalog.Default!.AreaHash16));
        Assert.False(PortalsFeature.RecreatedOnEntry(AreaLayouts.TheCoast));
    }
}

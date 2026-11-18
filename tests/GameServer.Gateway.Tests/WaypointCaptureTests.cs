namespace GameServer.Gateway.Tests;

/// <summary>
/// A capture of an area's tile objects names its waypoint only when the level placed one as a tile object. The
/// Endless Ledge and the areas whose seed-1 load the client fails do not, and neither did 1_1_5 The Ledge in the
/// capture recorded on 2026-11-13 — where the level generator's own objects do place a waypoint, at (1735, 895).
/// </summary>
public class WaypointCaptureTests
{
    [Fact]
    public void ACaptureThatNamesNoWaypointFallsBackToTheGeneratedOne()
    {
        // 1_1_5 at seed 1: three tile objects, one of them the waypoint, and the live capture holds only the
        // two area transitions (beachcaveentrance → 1_1_4_1, beach_passageway → 1_1_6).
        var capture = new WaypointCatalog.CaptureFileData([
            new("Metadata/MiscellaneousObjects/AreaTransition", "beachcaveentrance_v01_01_area_transition.ao", 670, 379, 0, 0),
            new("Metadata/MiscellaneousObjects/AreaTransition", "beach_passageway_v01_01_area_transition.ao", 548, 1866, 0, 0)]);
        Assert.Null(WaypointCatalog.FromCapture(capture));
        if (!File.Exists("data/game-files.zip")) return;
        var generated = WaypointCatalog.Generated("1_1_5", 1);
        Assert.NotNull(generated);
        Assert.StartsWith("Metadata/Terrain/Missions/", generated!.Model);
    }

    [Fact]
    public void ACaptureThatNamesAWaypointIsUsedAsItStands()
    {
        // 1_3_18_1's capture (2026-11-13): the tile objects name the waypoint the client placed.
        var capture = new WaypointCatalog.CaptureFileData([
            new("Metadata/MiscellaneousObjects/AreaTransition", "tower_transition_up_01_01_area_transition.ao", 1717, 185, 0, 0),
            new("Metadata/MiscellaneousObjects/Waypoint", "sceptre_waypoint_01_01_waypoint.ao", 2300, 621, 0, -15.517f)]);
        var waypoint = WaypointCatalog.FromCapture(capture);
        Assert.NotNull(waypoint);
        Assert.Equal((2300, 621), (waypoint!.GridX, waypoint.GridY));
        Assert.Equal(-15.517f, waypoint.Height);
    }
}

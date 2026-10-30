using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class CollisionEscapeTests
{
    [Fact]
    public void Moving_a_device_releases_its_old_cells_and_ejects_from_its_new_cells()
    {
        var conn = new ClientState(new(0, "test", 100, 100, 20, 20, (_, _) => true, []), 0, 1);
        conn.SetBlocker(7, 20, 20, ClientState.MapDeviceBlockerSize);
        Assert.NotNull(CollisionEscape.Find((20, 20), 100, 100, (_, _) => true, conn.StationaryBlockers));
        conn.SetBlocker(7, 40, 40, ClientState.MapDeviceBlockerSize);
        Assert.Null(CollisionEscape.Find((20, 20), 100, 100, (_, _) => true, conn.StationaryBlockers));
        var free = CollisionEscape.Find((40, 40), 100, 100, (_, _) => true, conn.StationaryBlockers)!.Value;
        Assert.True(CollisionEscape.Clear(free, 100, 100, (_, _) => true, conn.StationaryBlockers));
        Assert.Equal(5, Math.Max(Math.Abs(free.X - 40), Math.Abs(free.Y - 40)));
    }

}

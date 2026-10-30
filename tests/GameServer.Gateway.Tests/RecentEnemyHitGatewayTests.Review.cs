using System.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    static async Task WaitFor(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Expected the actual route's collection witness.");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(false)]
    public async Task Full_send_acknowledges_recent_values_and_tick_corrects_return_to_previous_tuple(bool fortification)
    {
        using var f = new Fixture();
        _ = f.Chance;
        double first = f.Conn.Clock.Elapsed.TotalSeconds - 2;
        f.Conn.RecentEnemyHits.Record(first);
        await f.Present(); await f.Read();
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        // Pure construction must not acknowledge a packet that was never sent.
        var built = (List<(uint, int)>)Method("PlayerStatsWithCleared").Invoke(null, [f.Conn])!;
        Assert.Contains((4534u, 2), built);
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        if (fortification)
        {
            Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 1);
            await Program.SendFortification("recent-full-send", f.Stream, f.Ciphers, f.Conn);
        }
        else await (Task)Method("SendCharacterStats").Invoke(null,
            ["recent-full-send", f.Stream, f.Ciphers, f.Conn, "recent counter repro"])!;
        await f.Read();
        Assert.Equal((2, 20), f.Conn.PresentedRecentEnemyHits);
        await WaitFor(() => f.Conn.Clock.Elapsed.TotalSeconds - first >= 4.02);
        Assert.Equal(1, f.Count);
        await f.Present();
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 1), (12268u, 10)]), await f.Read());
    }

}

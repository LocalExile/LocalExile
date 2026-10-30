using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{

    [Fact]
    public async Task Old_death_notification_cannot_clear_a_resurrected_players_new_contact()
    {
        using var f = new Fixture();
        await f.Command("/hit fire 1");
        await (Task)Method("PlayerDied").Invoke(null, ["incoming-test", f.Stream, f.Ciphers, f.Conn, "stale death"])!;
        Assert.Equal(1, f.Count); Assert.Equal(10, f.Chance);
    }

}

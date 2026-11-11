using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

public class ResyncTests
{
    [Fact]
    public void Resync_and_corpse_messages_have_the_clients_layout()
    {
        Assert.Equal("0128" + "02" + "00001000" + "0000014A" + "00000105" + "00003004" + "0000012C" + "00000100",
            Convert.ToHexString(InstanceProtocol.Resync([(0x1000, 330, 261), (0x3004, 300, 256)])));
        Assert.Equal("02B2" + "000000CE" + "000000CE" + "7000" + "00" + "00",
            Convert.ToHexString(InstanceProtocol.StopBlocking(0xCE)));
    }

    [Fact]
    public void A_held_walk_keeps_its_hold_through_a_speed_change_and_a_shorter_hold()
    {
        var walk = new PlayerWalk(0, 0, speed: 10);
        walk.Order(100, 0, now: 0);
        var held = walk.Position(1);
        walk.Pause(now: 1, seconds: 2);
        walk.SetSpeed(20, now: 1.5);
        walk.Pause(now: 1.6, seconds: .1);
        Assert.Equal(held, walk.Position(2.9));
        Assert.NotEqual(held, walk.Position(3.2));
    }
}

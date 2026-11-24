using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>The official death and resurrect wire order: the death screen carries the death count, the death message
/// names the character, the alive-state row says whether the actor is dead, and the checkpoint resurrect grants the
/// official 20 s grace. Bytes measured on official 3.29.3b captures; no real-time waits.</summary>
public class DeathResurrectOrderTests
{
    [Fact]
    public void Death_wire_order_bytes()
    {
        Assert.Equal("00550C000000000001", Convert.ToHexString(InstanceProtocol.DeathScreen(0x0C, 1)));
        Assert.Equal("001800C6FFFF0000000000000000000000010008546573744E616D65",
            Convert.ToHexString(InstanceProtocol.DeathMessage("TestName")));
        // Non-ASCII character names must encode with compact WStr (0x80 + UTF-16LE), matching 0x1429a60d0, not UTF-8.
        Assert.Equal("001800C6FFFF000000000000000000000001000180C900",
            Convert.ToHexString(InstanceProtocol.DeathMessage("É")));
        Assert.Equal("01F100000010FFFFFFFE00000101",
            Convert.ToHexString(InstanceProtocol.AliveState(0x10, 0xfffffffe, dead: true)));
        byte[] alive = InstanceProtocol.AliveState(0x10, 0, dead: false);
        Assert.Equal(14, alive.Length);
        Assert.Equal((byte)1, alive[12]);
        Assert.Equal((byte)0, alive[13]);
    }

    [Fact]
    public void Resurrect_grace_protects_for_20_seconds_at_the_revive_point()
    {
        var grace = new GracePeriod();
        Assert.True(grace.StartResurrect(100.0, (10, 20), GracePeriod.BuffRow, town: false, hideout: false));
        Assert.True(grace.Protects(118.0, (10, 20)));
        Assert.False(grace.Protects(122.0, (10, 20)));
        Assert.False(grace.StartResurrect(100.0, (0, 0), GracePeriod.BuffRow, town: true, hideout: false));
        Assert.False(grace.StartResurrect(100.0, (0, 0), GracePeriod.BuffRow, town: false, hideout: true));
    }
}

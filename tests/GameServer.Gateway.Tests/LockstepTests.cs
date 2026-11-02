using System.Net;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

public class LockstepTests
{
    static readonly IPEndPoint Local = new(IPAddress.Loopback, 6112), LocalV6 = new(IPAddress.IPv6Loopback, 6112),
        Lan = new(IPAddress.Parse("192.0.2.10"), 6112);
    const uint GivenLocal = 0x7f000001, GivenPublic = 0xcb007107;

    [Fact]
    public void Only_a_connection_confirmed_as_local_is_served()
    {
        Assert.Equal(LockstepMode.Local, LockstepLink.ModeOf("1"));
        Assert.Equal(LockstepMode.Network, LockstepLink.ModeOf("network"));
        Assert.Equal(LockstepMode.Off, LockstepLink.ModeOf("0"));
        Assert.Equal(LockstepMode.Off, LockstepLink.ModeOf(null));
        Assert.True(LockstepLink.Confirmed(LockstepMode.Local, Local, Local, GivenLocal));
        Assert.True(LockstepLink.Confirmed(LockstepMode.Local, LocalV6, LocalV6, GivenLocal));
        Assert.False(LockstepLink.Confirmed(LockstepMode.Off, Local, Local, GivenLocal));
        Assert.False(LockstepLink.Confirmed(LockstepMode.Local, Lan, Local, GivenLocal));
        Assert.False(LockstepLink.Confirmed(LockstepMode.Local, Local, Lan, GivenLocal));
        Assert.False(LockstepLink.Confirmed(LockstepMode.Local, Lan, Lan, GivenPublic));
        // A tunnel on the server's side: from 127.0.0.1, but the client was told another address.
        Assert.False(LockstepLink.Confirmed(LockstepMode.Local, Local, Local, GivenPublic));
        Assert.False(LockstepLink.Confirmed(LockstepMode.Local, null, Local, GivenLocal));
        Assert.True(LockstepLink.Confirmed(LockstepMode.Network, Lan, Lan, GivenPublic));
        Assert.False(LockstepLink.Confirmed(LockstepMode.Off, Lan, Lan, GivenPublic));
        var network = LockstepLatency.Unconditional();
        for (int i = 0; i < LockstepLatency.Window; i++) network.Add(300);
        Assert.True(network.Confirmed && !network.Refused && network.Settled);

        // A tunnel on both ends: the lowest round trip decides, by the client's own 70 and 90 ms, and only with enough of them.
        var fast = LockstepLatency.For("fast");
        for (int i = 0; i < LockstepLatency.Enough - 1; i++) fast.Add(104);
        Assert.False(fast.Confirmed || fast.Refused);
        Assert.True(fast.Add(12));
        Assert.True(fast.Confirmed);
        var slow = LockstepLatency.For("slow");
        for (int i = 0; i < LockstepLatency.Enough; i++) slow.Add(95);
        Assert.True(slow.Refused && !slow.Confirmed);
        var between = LockstepLatency.For("between");
        for (int i = 0; i < LockstepLatency.Window; i++) between.Add(80);
        Assert.True(between.Refused);
    }

    [Fact]
    public void A_held_walk_follows_its_newest_target_and_tries_again_when_stopped()
    {
        byte[] requests = Convert.FromHexString("018F000001F10266040000" + "01960266");
        var framed = InstanceProtocol.SplitClientMessages(requests, out _, out var unknown);
        Assert.Equal(new ushort[] { 0x018f, 0x0196 }, framed.Select(m => m.Id));
        Assert.Empty(unknown);
        Assert.All(framed, m => Assert.True(InstanceProtocol.IsLockstepRequest(m.Id)));
        Assert.Equal(((uint)0x1f1, (ushort)0x266, (ushort)0x400, (byte)0), InstanceProtocol.ReadLockstepUseOnObject(framed[0].Bytes));

        var hold = new LockstepHold();
        var (here, walking) = ((0, 0), true);
        Assert.Null(hold.Due(walking, here));

        hold.Press((10, 0), 0x2909, 0x0400);
        Assert.Equal((10, 0), hold.Due(walking, here));
        Assert.Null(hold.Due(walking, here));

        hold.Update((10, 0));
        Assert.Null(hold.Due(walking, here));
        hold.Update((12, 0));
        Assert.Equal((12, 0), hold.Due(walking, here));

        // Stopped short while held: again every frame. Arrived: nothing.
        Assert.Equal((12, 0), hold.Due(false, (4, 0)));
        Assert.Equal((12, 0), hold.Due(false, (4, 0)));
        Assert.Null(hold.Due(false, (12, 0)));

        // A release does not cancel a target not yet started, and ends the retries.
        hold.Update((20, 0));
        hold.Release();
        Assert.Equal((20, 0), hold.Due(true, (4, 0)));
        Assert.Null(hold.Due(false, (4, 0)));

        hold.Press((30, 0), 0x2909, 0x0400);
        Assert.Equal((30, 0), hold.Due(true, here));
        hold.Release();
        hold.Update((31, 0));
        Assert.Null(hold.Due(false, here));

        // An object use takes over from a held walk.
        hold.Press((40, 0), 0x2909, 0x0400);
        hold.Clear();
        Assert.Null(hold.Due(false, here));

        // A skill waits for a free player, repeats while held and goes on at the ground when its target dies.
        var skill = new LockstepSkillHold();
        skill.Press((5, 5), 0x3007, 0x0102, 0x0401, repeats: true);
        Assert.False(skill.Due(free: false));
        Assert.True(skill.Due(free: true));
        Assert.True(skill.Due(free: true));
        skill.LoseTarget();
        skill.Update((6, 6));
        Assert.Null(skill.Target);
        Assert.Equal((6, 6), skill.Aim);
        skill.Release();
        Assert.False(skill.Due(free: true));

        // A click during a swing is kept through its own release.
        skill.Press((7, 7), null, 0x0102, 0x0001, repeats: true);
        skill.Release();
        Assert.False(skill.Due(free: false));
        Assert.True(skill.Due(free: true));

        // A channel starts once and ends on the release.
        skill.Press((8, 8), null, 0x0200, 0x0401, repeats: false);
        Assert.True(skill.Due(free: true));
        skill.Channelling(0x8001);
        Assert.False(skill.Due(free: true));
        Assert.Null(skill.ChannelReleased());
        skill.Release();
        Assert.Equal((ushort)0x8001, skill.ChannelReleased());
        Assert.Equal("01AE00001000000000000000800101", Convert.ToHexString(InstanceProtocol.ChannelFinished(0x1000, 0x8001)));
    }
}

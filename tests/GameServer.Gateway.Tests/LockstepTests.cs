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
        Assert.True(LockstepLink.Confirmed(true, Local, Local, GivenLocal));
        Assert.True(LockstepLink.Confirmed(true, LocalV6, LocalV6, GivenLocal));
        Assert.False(LockstepLink.Confirmed(false, Local, Local, GivenLocal));
        Assert.False(LockstepLink.Confirmed(true, Lan, Local, GivenLocal));
        Assert.False(LockstepLink.Confirmed(true, Local, Lan, GivenLocal));
        Assert.False(LockstepLink.Confirmed(true, Lan, Lan, GivenPublic));
        // A tunnel on the server's side: from 127.0.0.1, but the client was told another address.
        Assert.False(LockstepLink.Confirmed(true, Local, Local, GivenPublic));
        Assert.False(LockstepLink.Confirmed(true, null, Local, GivenLocal));
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
    }
}

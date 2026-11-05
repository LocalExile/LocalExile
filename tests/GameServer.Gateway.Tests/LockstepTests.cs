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
    public void A_walker_on_frames_starts_on_the_frame_and_stops_after_its_steps()
    {
        // 100 cells a second: one cell every 10 ms, 3.3 cells a frame.
        double start = 0;
        PlayerWalk Walker(double speed = 100) => new(0, 0, speed) { FrameOf = at => (Math.Min(at, start), start + .033) };

        var walk = Walker();
        walk.Order(100, 0, .005);   // sent 5 ms into the frame, it starts at the frame's start
        start = .033;
        Assert.Equal((3, 0), walk.Position(.04));   // read during a frame, it is where the frame began
        Assert.Equal((3, 0), walk.Order(3, 0, .04));   // the stop reports where the client is now
        Assert.Equal((6, 0), walk.Position(.04));   // and the walker stands where the frame's steps take it
        Assert.False(walk.Moving(.1));

        start = 0;
        walk = Walker();
        walk.Order(100, 0, 0);
        start = .033;
        walk.Pause(.04, .1);   // a hold also starts after the frame's steps, and ends .1 s after the frame's start
        start = .099;
        Assert.Equal((6, 0), walk.Position(.099));
        start = .165;
        Assert.Equal((9, 0), walk.Position(.165));

        // A stop keeps the time since the last step, and the next walk from the stand starts with it (40 cells a second:
        // 25 ms a step; the client's accumulator 16 ms, + 33 ms is one step, + 33 ms two more).
        start = 0; walk = Walker(40);
        walk.Order(100, 0, 0);
        start = .033; Assert.Equal((1, 0), walk.Order(1, 0, .04));   // stands at (2, 0) after the frame, 16 ms into its step
        start = .099; walk.Order(100, 0, .099);
        start = .165; Assert.Equal((5, 0), walk.Position(.165));

        // The same through a walk-up found in reach where the walker stands, which stops where the frame's steps leave it,
        // and a forced set at the stand keeps it.
        start = 0; walk = Walker(40);
        walk.Order(100, 0, 0);
        start = .033; var (here, stop, _) = walk.Approach(11, 0, 10, .04);
        Assert.Equal(((1, 0), (2, 0)), (here, stop));
        start = .066; walk.Teleport(2, 0, .07);
        start = .099; walk.Order(100, 0, .099);
        start = .165; Assert.Equal((5, 0), walk.Position(.165));

        // Kept time longer than the new step (30.6 ms into a 35.4 ms diagonal step, then 25 ms straight ones): the step
        // already due comes in the order's frame, not before it; the client steps 2, 1 and 2 cells in the next frames.
        start = 0; walk = Walker(40);
        walk.Order(20, 20, 0);
        start = .033; Assert.Equal((0, 0), walk.Order(0, 0, .04));
        Assert.Equal((1, 1), walk.Destination);
        start = .099; Assert.Equal((1, 1), walk.Order(20, 1, .099));
        Assert.Equal((1, 1), walk.Position(.099));
        Assert.Equal([.1087, .1184, .1434], walk.Steps(.05, .16).Select(s => Math.Round(s.At, 4)));
        Assert.Equal([(2, 1), (3, 1)], walk.Steps(.099, .132).Select(s => s.Cell));
        start = .132; Assert.Equal((3, 1), walk.Position(.132));
        start = .165; Assert.Equal((4, 1), walk.Position(.165));
        start = .198; Assert.Equal((6, 1), walk.Position(.198));

        // A slow walker's too (10 cells a second: 132 ms kept, 100 ms steps): in the order's frame, not the next one.
        start = 0; walk = Walker(10);
        walk.Order(20, 20, 0);
        start = .099; walk.Order(0, 0, .1);
        start = .165; walk.Order(20, 0, .165);
        Assert.Equal([(1, 0)], walk.Steps(.165, .198).Select(s => s.Cell));
        start = .231; Assert.Equal((1, 0), walk.Position(.231));
        start = .264; Assert.Equal((2, 0), walk.Position(.264));

        // A speed doubled at the stand (12.5 ms steps against 30.6 ms kept) leaves two steps due: both come in the order's
        // frame, in order, before the walk's third step, and the client takes 5 steps in that frame's tick and 2 in the next.
        start = 0; walk = Walker(40);
        walk.Order(20, 20, 0);
        start = .033; walk.Order(0, 0, .04);
        walk.SetSpeed(80, .05);
        start = .099; Assert.Equal((1, 1), walk.Order(20, 1, .099));
        Assert.Equal((1, 1), walk.Position(.099));
        Assert.Equal([.1013, .1036, .1059, .1184, .1309], walk.Steps(.099, .132).Select(s => Math.Round(s.At, 4)));
        Assert.Equal([(2, 1), (3, 1), (4, 1), (5, 1), (6, 1)], walk.Steps(.099, .132).Select(s => s.Cell));
        start = .165; Assert.Equal((8, 1), walk.Position(.165));
    }

    [Fact]
    public void A_player_attack_walks_up_to_the_weapon_range_less_one()
    {
        // A Drowned (size 2, the tolerance) 13 cells off and a sword of range 11: the client's MoveTo has max range 10, so
        // the player is in reach only 12 cells off, one step on (the whole range would have it swing where it stands).
        var conn = new ClientState(new(1, "walk-up", 300, 300, 100, 100, null, []), 1, 1);
        var drowned = new SpawnedMonster("Drowned", new PlayerWalk(113, 100, 8, size: 2), 0, 4, 100);
        var sword = new SkillCatalog.Unarmed(645, 1, 2, 11);
        var (here, stop, _) = Game.WalkUpToSwing(conn, drowned, (113, 100), sword, 0, 1);
        Assert.Equal(((100, 100), (101, 100)), (here, stop));
    }

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

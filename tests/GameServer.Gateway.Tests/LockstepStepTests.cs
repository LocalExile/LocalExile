using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

// The step pass on a lockstep client replays whole frames, each with every walker in ascending object id, the player
// first, against the cells the others hold at that moment, as the client's world ticks (07 §5); a predictive client's
// steps are replayed in time order as before. Frames are 33 ms long and end at 0.033, 0.066, 0.099, 0.132, ...
public class LockstepStepTests
{
    static ClientState Connection(bool lockstep, (double Start, double End) sending) => new(new(1, "steps", 300, 300, 100, 100, null, []), 1, 1)
    {
        Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500, PlayerSent = true, LockstepClient = lockstep, LockstepFrame = sending,
    };

    // Without a variety a monster's body is size 2, as its walk is here (NewMonsterWalk gives both the same size): two of them
    // block each other within four cells, the player and a monster within three.
    static SpawnedMonster Monster(int x, int y, double speed) => new("Drowned", new PlayerWalk(x, y, speed, size: 2), aggroRange: 60, stopRange: 2, maxLife: 23);

    [Fact]
    public void A_lockstep_frame_steps_its_walkers_in_object_order_and_whole_frames_only()
    {
        // 0x3000's first step comes at 0.105 s, 0x3001's at 0.100 s: both in the frame that ends at 0.132.
        var conn = Connection(lockstep: true, sending: (0.132, 0.165));
        var a = Monster(50, 50, 1 / 0.105);
        var b = Monster(50, 56, 10);
        a.Walk.Order(50, 70, now: 0);
        b.Walk.Order(50, 36, now: 0);
        conn.Monsters[0x3000] = a;
        conn.Monsters[0x3001] = b;
        lock (conn.Monsters) Game.StepWalkers(conn, now: 0.14);
        // The frame being sent waits for its end.
        Assert.Equal(0.132, conn.WalkersSteppedTo, 9);
        // 0x3000 ticks first and takes (50, 51), five cells from 0x3001; then 0x3001's step to (50, 55), four cells from
        // it, is blocked and ends its walk.
        Assert.Equal((50, 51), a.Walk.Position(0.132));
        Assert.True(a.Walk.Moving(0.2));
        Assert.Equal((50, 56), b.Walk.Position(0.132));
        Assert.False(b.Walk.Moving(0.2));

        // A stop waits for the tick, and the step that tick still owes is checked like any other. 0x3001 is ordered onto
        // its own cell in the frame from 0.099: its step to (50, 55) at 0.100 is still due, and 0x3000's body, ticked
        // first, blocks it, so it never leaves (50, 56).
        var stopping = Connection(lockstep: true, sending: (0.099, 0.132));
        (double Start, double End) sent = (0.099, 0.132);
        a = Monster(50, 50, 1 / 0.105);
        b = new SpawnedMonster("Drowned", new PlayerWalk(50, 56, 10, size: 2) { FrameOf = at => (Math.Min(at, sent.Start), sent.End) },
            aggroRange: 60, stopRange: 2, maxLife: 23);
        a.Walk.Order(50, 70, now: 0);
        b.Walk.Order(50, 36, now: 0);
        stopping.Monsters[0x3000] = a;
        stopping.Monsters[0x3001] = b;
        Assert.Equal((50, 56), b.Walk.Order(50, 56, now: 0.1));
        stopping.LockstepFrame = sent = (0.132, 0.165);
        lock (stopping.Monsters) Game.StepWalkers(stopping, now: 0.14);
        Assert.Equal((50, 51), a.Walk.Position(0.132));
        Assert.Equal((50, 56), b.Walk.Position(0.132));
        Assert.False(b.Walk.Moving(0.132));

        // A predictive client's steps are replayed in time order: 0x3001's at 0.100 goes first and 0x3000's is blocked.
        var predictive = Connection(lockstep: false, sending: (0.132, 0.165));
        a = Monster(50, 50, 1 / 0.105);
        b = Monster(50, 56, 10);
        a.Walk.Order(50, 70, now: 0);
        b.Walk.Order(50, 36, now: 0);
        predictive.Monsters[0x3000] = a;
        predictive.Monsters[0x3001] = b;
        lock (predictive.Monsters) Game.StepWalkers(predictive, now: 0.14);
        Assert.Equal(0.14, predictive.WalkersSteppedTo);
        Assert.Equal((50, 50), a.Walk.Position(0.14));
        Assert.False(a.Walk.Moving(0.2));
        Assert.Equal((50, 55), b.Walk.Position(0.14));
    }

    // A lockstep client runs its own copy of another player, so the estimate runs it too: the other player's order
    // starts from the cell the copy stands on, whatever cell its owner's estimate reported, and the copy steps in this
    // client's frames after the local player (a higher id), a body like any other.
    [Fact]
    public void Another_players_copy_walks_its_orders_in_the_observers_frames()
    {
        const uint peer = 0x20000005;
        var conn = Connection(lockstep: true, sending: (0.033, 0.066));
        conn.Walk.Teleport(50, 50, 0);
        lock (conn.Monsters)
        {
            var copy = conn.PeerWalks[peer] = conn.NewPeerWalk(peer, 60, 50, 37);
            // The owner was sent a Move from (61, 50), a cell ahead of the copy: this client gets it from (60, 50).
            Assert.Equal(GameServer.Core.Protocol.Instance.InstanceProtocol.ActionStarted(peer, 60, 50, 80, 50, 0x2909, 0x8005, 0x0408),
                Game.PeerRecordAtGate(conn, peer,
                    GameServer.Core.Protocol.Instance.InstanceProtocol.ActionStarted(peer, 61, 50, 80, 50, 0x2909, 0x8005, 0x0408), 37));
            // 37 cells a second, a step every 27 ms: one in the frame from 0.033, one in the frame from 0.066.
            conn.LockstepFrame = (0.099, 0.132);
            Game.StepWalkers(conn, now: 0.1);
            Assert.Equal((62, 50), copy.Position(0.099));
            // Another action ends its walk after the frame's step; the order again names the copy's cell.
            Assert.Equal(GameServer.Core.Protocol.Instance.InstanceProtocol.ActionStarted(peer, 62, 50, 70, 60, 0x0102, 0x8006, 0x0408),
                Game.PeerRecordAtGate(conn, peer,
                    GameServer.Core.Protocol.Instance.InstanceProtocol.ActionStarted(peer, 64, 50, 70, 60, 0x0102, 0x8006, 0x0408), 37));
            conn.LockstepFrame = (0.198, 0.231);
            Game.StepWalkers(conn, now: 0.2);
            Assert.Equal((63, 50), copy.Position(0.198));
            Assert.False(copy.Moving(0.198));
            // The local player walks into it: its body stops him three cells short, as a monster's would.
            conn.Walk.Order(63, 50, now: 0.198);
            conn.LockstepFrame = (0.990, 1.023);
            Game.StepWalkers(conn, now: 1);
            Assert.Equal((60, 50), conn.Walk.Position(0.990));
            Assert.False(conn.Walk.Moving(0.990));
            // His own search for a way past sees the copy's cell too, and goes round it.
            conn.Walk.Order(70, 50, now: 0.990);
            conn.LockstepFrame = (2.970, 3.003);
            Game.StepWalkers(conn, now: 2.98);
            Assert.Equal((70, 50), conn.Walk.Position(2.970));
        }
    }

    [Fact]
    public void The_player_ticks_before_the_monsters_in_a_lockstep_frame()
    {
        // The player (37 cells/s) steps east to (51, 50) at 0.027 s; the monster (100 cells/s) steps north to (54, 53) at
        // 0.010 s, three cells from that: whoever steps first blocks the other.
        var conn = Connection(lockstep: true, sending: (0.033, 0.066));
        conn.Walk.Teleport(50, 50, 0);
        conn.Walk.Order(70, 50, now: 0);
        var b = Monster(54, 54, 100);
        b.Walk.Order(54, 34, now: 0);
        conn.Monsters[0x3000] = b;
        lock (conn.Monsters) Game.StepWalkers(conn, now: 0.04);
        Assert.Equal(0.033, conn.WalkersSteppedTo, 9);
        Assert.Equal((51, 50), conn.Walk.Position(0.033));
        Assert.True(conn.Walk.Moving(0.03));
        Assert.Equal((54, 54), b.Walk.Position(0.033));
        Assert.False(b.Walk.Moving(0.1));
    }
}

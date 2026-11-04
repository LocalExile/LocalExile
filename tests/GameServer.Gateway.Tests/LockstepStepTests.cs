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

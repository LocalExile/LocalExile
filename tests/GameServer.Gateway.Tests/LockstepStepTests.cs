using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

// The step pass on a lockstep client replays whole frames, each with every walker in ascending object id, the player
// first, against the cells the others hold at that moment, as the client's world ticks (07 §5); a predictive client's
// steps are replayed in time order as before. Frames are 33 ms long and end at 0.033, 0.066, 0.099, 0.132, ...
public class LockstepStepTests
{
    static Player PlayerOf(bool lockstep, (double Start, double End) sending, uint id = 0x20001000) => new(new(1, "steps", 300, 300, 100, 100, null, []), 1, 1)
    {
        Id = id,
        Pools = new(1, 1000, 100, 2, 0),
        PlayerLife = 500,
        Connection = { PlayerSent = true, LockstepClient = lockstep, LockstepFrame = sending }
    };

    // Without a variety a monster's body is size 2, as its walk is here (NewMonsterWalk gives both the same size): two of them
    // block each other within four cells, the player and a monster within three.
    static SpawnedMonster Monster(int x, int y, double speed) => new("Drowned", new PlayerWalk(x, y, speed, size: 2), aggroRange: 60, stopRange: 2, maxLife: 23);

    [Fact]
    public void A_lockstep_frame_steps_its_walkers_in_object_order_and_whole_frames_only()
    {
        // 0x3000's first step comes at 0.105 s, 0x3001's at 0.100 s: both in the frame that ends at 0.132.
        var player = PlayerOf(lockstep: true, sending: (0.132, 0.165));
        var a = Monster(50, 50, 1 / 0.105);
        var b = Monster(50, 56, 10);
        a.Walk.Order(50, 70, now: 0);
        b.Walk.Order(50, 36, now: 0);
        player.Area.Monsters[0x3000] = a;
        player.Area.Monsters[0x3001] = b;
        Game.StepWalkers(player, now: 0.14);
        // The frame being sent waits for its end.
        Assert.Equal(0.132, player.WalkersSteppedTo, 9);
        // 0x3000 ticks first and takes (50, 51), five cells from 0x3001; then 0x3001's step to (50, 55), four cells from
        // it, is blocked and ends its walk.
        Assert.Equal((50, 51), a.Walk.Position(0.132));
        Assert.True(a.Walk.Moving(0.2));
        Assert.Equal((50, 56), b.Walk.Position(0.132));
        Assert.False(b.Walk.Moving(0.2));

        // A stop waits for the tick, and the step that tick still owes is checked like any other. 0x3001 is ordered onto
        // its own cell in the frame from 0.099: its step to (50, 55) at 0.100 is still due, and 0x3000's body, ticked
        // first, blocks it, so it never leaves (50, 56).
        var stopping = PlayerOf(lockstep: true, sending: (0.099, 0.132));
        (double Start, double End) sent = (0.099, 0.132);
        a = Monster(50, 50, 1 / 0.105);
        b = new SpawnedMonster("Drowned", new PlayerWalk(50, 56, 10, size: 2) { FrameOf = at => (Math.Min(at, sent.Start), sent.End) },
            aggroRange: 60, stopRange: 2, maxLife: 23);
        a.Walk.Order(50, 70, now: 0);
        b.Walk.Order(50, 36, now: 0);
        stopping.Area.Monsters[0x3000] = a;
        stopping.Area.Monsters[0x3001] = b;
        Assert.Equal((50, 56), b.Walk.Order(50, 56, now: 0.1));
        stopping.Connection.LockstepFrame = sent = (0.132, 0.165);
        Game.StepWalkers(stopping, now: 0.14);
        Assert.Equal((50, 51), a.Walk.Position(0.132));
        Assert.Equal((50, 56), b.Walk.Position(0.132));
        Assert.False(b.Walk.Moving(0.132));

        // An in-place attack leaves a walk going under it, and the client's Actor stops that walk when it ends the
        // attack: a 0.99 s attack ordered in the frame from 0.132 uses up 31 frames' time with its own frame, so the
        // walk (a cell each 0.1 s) is stopped in the frame from 1.155, 31 after it, and stands on its 11th cell with
        // the step due at 1.2 never taken. A predictive client's walk is left as it is.
        foreach (bool lockstep in new[] { true, false })
        {
            var shooting = PlayerOf(lockstep, sending: (0.132, 0.165));
            var spitter = Monster(50, 50, 10);
            var spit = new MonsterAttack(0x1535, 0.99, 30, DamagePacket.Empty) { Ranged = true };
            spitter.Walk.Order(50, 90, now: 0);
            shooting.Area.Monsters[0x3000] = spitter;
            Assert.NotNull(Game.MonsterSwingOrderLocked(shooting, spitter, spit, action: 1, generic: false, seconds: 0.99, now: 0.14));
            Assert.Equal(lockstep ? 0.132 + 31 * 0.033 : null, spitter.ClientActionWalkStop);
            shooting.Connection.LockstepFrame = (1.155, 1.188);
            Game.StepWalkers(shooting, now: 1.16);
            Assert.True(spitter.Walk.Moving(1.155));
            shooting.Connection.LockstepFrame = (1.188, 1.221);
            Game.StepWalkers(shooting, now: 1.19);
            Assert.Equal(lockstep, !spitter.Walk.Moving(1.19));
            Assert.Equal(lockstep ? (50, 61) : (50, 64), spitter.Walk.Position(1.45));
            Assert.Null(spitter.ClientActionWalkStop);
        }

        // A predictive client's steps are replayed in time order: 0x3001's at 0.100 goes first and 0x3000's is blocked.
        var predictive = PlayerOf(lockstep: false, sending: (0.132, 0.165));
        a = Monster(50, 50, 1 / 0.105);
        b = Monster(50, 56, 10);
        a.Walk.Order(50, 70, now: 0);
        b.Walk.Order(50, 36, now: 0);
        predictive.Area.Monsters[0x3000] = a;
        predictive.Area.Monsters[0x3001] = b;
        Game.StepWalkers(predictive, now: 0.14);
        Assert.Equal(0.14, predictive.WalkersSteppedTo);
        Assert.Equal((50, 50), a.Walk.Position(0.14));
        Assert.False(a.Walk.Moving(0.2));
        Assert.Equal((50, 55), b.Walk.Position(0.14));
    }

    [Fact]
    public void Walkers_tick_in_ascending_id_order_in_a_lockstep_frame()
    {
        // The player (37 cells/s) steps east to (51, 50) at 0.027 s; the monster (100 cells/s) steps north to (54, 53) at
        // 0.010 s, three cells from that: whoever ticks first blocks the other. A player is 0x20000000 on, after the
        // monsters; this one is given an id below the monster's.
        var player = PlayerOf(lockstep: true, sending: (0.033, 0.066), id: 0x1000);
        player.Walk.Teleport(50, 50, 0);
        player.Walk.Order(70, 50, now: 0);
        var b = Monster(54, 54, 100);
        b.Walk.Order(54, 34, now: 0);
        player.Area.Monsters[0x3000] = b;
        Game.StepWalkers(player, now: 0.04);
        Assert.Equal(0.033, player.WalkersSteppedTo, 9);
        Assert.Equal((51, 50), player.Walk.Position(0.033));
        Assert.True(player.Walk.Moving(0.03));
        Assert.Equal((54, 54), b.Walk.Position(0.033));
        Assert.False(b.Walk.Moving(0.1));
    }
}

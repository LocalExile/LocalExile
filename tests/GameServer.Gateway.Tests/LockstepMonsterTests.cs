using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core.Projectiles;
using GameServer.Modding;
using Xunit;

namespace GameServer.Gateway.Tests;

public class LockstepMonsterTests : IDisposable
{
    // The planner only works under the Game module's own lifetime, with its logic installed (CurrentGameWork), as the
    // Behemoth tests set them.
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    public LockstepMonsterTests()
    {
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
    }
    public void Dispose()
    {
        lifetime.Stop(ex => throw ex);
        Game.CombatLifetime = previousLifetime;
        if (previousLogic is not null) Program.SetGameLogic(previousLogic);
    }

    static ClientState Connection(bool lockstep, string? character = null) => new(new(1, "chase", 300, 300, 100, 100, null, []), 1, 1)
    {
        Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500, PlayerSent = true, LockstepClient = lockstep, Character = character,
    };

    static SpawnedMonster Drowned(PlayerWalk walk) => new("Drowned", walk, aggroRange: 60, stopRange: 2, maxLife: 23)
    {
        Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Aggro = true, LastTarget = (100, 100),
    };

    // A lockstep client's chase walk-up (the attack aimed at the player) swings by itself in the frame its own goal test
    // finds the player in reach (07 §5): where it stands when the order applies, after its steps in a frame against the
    // player's cell as his tick left it, or where a blocked step stopped it; out of reach at its path's end it searches
    // again toward the player. The server takes that swing from that frame's start with no order, and gives a standing
    // lockstep melee monster in reach the chase, not a swing order. A monster in a hit's recovery is ordered only from the
    // frame after the one its tick ends the recovery in. Only a chase the client runs is tested, not a Move that sets
    // LastTarget; an in-place attack, a monster that never walks and a predictive client keep their swing orders. A
    // lockstep in-place attack starts at the start of the frame its order goes out in, from the cell the steps before
    // that leave the monster on.
    [Fact]
    public void A_chase_walk_up_swings_in_the_frame_the_client_finds_the_player_in_reach()
    {
        static SpawnedMonster Chasing(SpawnedMonster m)
        {
            m.ClientChase = m.ActionGeneration;   // its chase order went out
            return m;
        }
        // Its own step: (108, 100) at 0.25 is 8 cells from the player, in the frame from 0.231; the stop waits for the next
        // tick's steps, and the next is due at 0.375, so it stands there.
        var conn = Connection(lockstep: true);
        conn.LockstepFrame = (1.023, 1.056);
        var arrived = Chasing(Drowned(new PlayerWalk(110, 100, 8)));
        arrived.Walk.Order(104, 100, now: 0);   // a step every 0.125 s
        var walking = Drowned(new PlayerWalk(140, 100, 8));
        walking.Walk.Order(100, 100, now: 1);   // 40 cells from the player
        var blocked = Drowned(new PlayerWalk(130, 100, 8));
        blocked.Walk.Order(100, 100, now: 0);
        blocked.Walk.StopAt(0.5);   // a step found blocked half a second in, 27 cells from the player
        // Standing 4 cells off with no chase open: the chase, not a swing order. A walk-up whose first step, at 0.025,
        // runs into that body tests the goal where it stopped, 8 cells off: it swings, from the first frame.
        var standing = Drowned(new PlayerWalk(100, 104, 8));
        standing.LastTarget = null;
        var stopped = Chasing(Drowned(new PlayerWalk(100, 108, 8)));
        stopped.Walk.Order(100, 100, now: -0.1);
        // A melee monster that never walks (MovementSpeed 0, a vine) gets no chase: in reach it keeps the swing order.
        var vine = new SpawnedMonster("Vine", new PlayerWalk(105, 105, 8), aggroRange: 60, stopRange: 2, maxLife: 23)
        {
            Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Aggro = true,
            Variety = new MonsterCatalog.Variety(0, "Vine", "Vine", "Vine", null, null, MovementSpeed: 0),
        };
        conn.Monsters[0x3000] = arrived;
        conn.Monsters[0x3001] = walking;
        conn.Monsters[0x3005] = blocked;
        conn.Monsters[0x3006] = standing;
        conn.Monsters[0x3007] = stopped;
        conn.Monsters[0x3009] = vine;
        // Faster walkers (a step every 0.025 s straight on). Round a wall's corner, the waypoint (106, 95) at 0.275 is the
        // first cell in reach: it stands there at once, though its next step is due at 0.308, in the next frame. Straight
        // on, (94, 96) at 0.600 and (95, 96) at 0.625 come within reach in the frame from 0.594, mid-piece: the stop takes
        // the next frame's step at 0.650 to (96, 96) and keeps the 10 ms since it for the next walk.
        var cornering = Chasing(Drowned(new PlayerWalk(106, 84, 40, walkable: (x, y) => x is < 92 or > 105 || y is < 80 or > 95)));
        cornering.Walk.Order(99, 101, now: 0);
        var sprinter = Chasing(Drowned(new PlayerWalk(70, 96, 40)));
        sprinter.Walk.Order(99, 96, now: 0);
        conn.Monsters[0x300b] = cornering;
        conn.Monsters[0x300c] = sprinter;
        var plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 1.03);
        Assert.Equal([0x3000u, 0x3007u, 0x300bu, 0x300cu], plan.ArrivedSwings.Select(s => s.Id));
        Assert.Equal(0.264, plan.ArrivedSwings[2].BegunAt, 3);
        Assert.Equal((106, 95), cornering.Walk.Position(1.03));
        Assert.Equal(0.594, plan.ArrivedSwings[3].BegunAt, 3);
        Assert.Equal((96, 96), sprinter.Walk.Position(1.03));
        sprinter.Walk.Order(99, 96, now: 1.03);
        Assert.Equal(1.03 - 0.010 + 0.025, sprinter.Walk.Steps(1.03, 2)[0].At, 4);
        Assert.Equal(0.231, plan.ArrivedSwings[0].BegunAt, 3);
        Assert.Equal(0.231 + 2.0 + conn.LockstepActionTail, arrived.BusyUntil, 3);
        Assert.Equal((108, 100), arrived.Walk.Position(1.03));
        Assert.False(arrived.Walk.Moving(1.03));
        Assert.Null(arrived.LastTarget);
        Assert.Equal(arrived.ActionGeneration, arrived.SwingGeneration);
        Assert.Equal(0.0, plan.ArrivedSwings[1].BegunAt, 3);
        Assert.Equal((100, 108), stopped.Walk.Position(1.03));
        Assert.Equal([0x3001u, 0x3005u], plan.Legs.Select(c => c.Id));   // far off: ordered again, in legs
        Assert.Equal([0x3006u], plan.Chases.Select(c => c.Id));
        Assert.Equal([0x3009u], plan.Swings.Select(s => s.Id));
        // The order's own apply: in reach where the monster stands, the swing starts in the order's frame and no walk-up
        // opens; out of reach, the walk-up opens for the step pass.
        var near = Drowned(new PlayerWalk(106, 100, 8));
        Assert.Equal(((106, 100), (100, 100)), Game.PlaceLockstepChase(conn, near, 7, 1.023));
        Assert.Equal(1.023, near.ClientSwingAt);
        Assert.Equal(7, near.ClientSwingAction);
        Assert.Null(near.ClientChase);
        var far = Drowned(new PlayerWalk(130, 100, 8));
        Game.PlaceLockstepChase(conn, far, 7, 1.023);
        Assert.Equal(7, far.ClientChase);
        Assert.Null(far.ClientSwingAt);
        Assert.Equal((100, 100), far.LastTarget);
        // A hit's recovery holds the client's monster (0x141e941a0): a chase that comes meanwhile waits, and the monster's
        // own tick starts it in the frame that ends the recovery, after the player's tick. A 1.0 s 0x1d1 written in the
        // frame from 1.023 ends in the frame 31 later, from 2.046: nothing is ordered in it, even with the clock past its
        // end before its 0x2b7 has gone out, and the chase in the next frame, from 2.079, starts at once and tests the
        // player's cell after his step in the frame from 2.046, 8.1 cells off (7.3 before it), so it walks up instead of
        // swinging.
        var hurtConn = Connection(lockstep: true);
        hurtConn.Walk.Order(140, 140, now: 2.013);   // his first step, diagonal, at 2.051
        var hurt = new SpawnedMonster("Drowned", new PlayerWalk(93, 102, 8), aggroRange: 60, stopRange: 2, maxLife: 23)
        {
            Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Aggro = true, HitAnimations = [new(0, 1.0)],
        };
        hurtConn.Monsters[0x3000] = hurt;
        hurtConn.LockstepFrame = (1.023, 1.056);
        lock (hurtConn.Monsters)
        {
            // This scenario fixes the native recovery duration; the server's stun formula is tested separately.
            hurt.FlinchUntil = 2.024;
            Game.TakeHitPacket(hurtConn, 0x3000, hurt, hurt.FlinchUntil, 1.024);   // its send gate
        }
        Assert.Equal(1.023 + 32 * 0.033, hurt.FlinchUntil, 9);
        hurtConn.LockstepFrame = (2.046, 2.079);
        Assert.Empty(Game.PlanMonsterAi(hurtConn, new MonsterAiCadence(), now: 2.08).Chases);
        hurtConn.LockstepFrame = (2.079, 2.112);
        var (_, _, recovered, _, _) = Assert.Single(Game.PlanMonsterAi(hurtConn, new MonsterAiCadence(), now: 2.083).Chases);
        Assert.Equal(((93, 102), (101, 101)), Game.PlaceLockstepChase(hurtConn, hurt, recovered, 2.079));
        Assert.Null(hurt.ClientSwingAt);
        Assert.Equal(recovered, hurt.ClientChase);
        // A type the client never walks (Pathfinding immobile: a unique Maven's orb, MovementSpeed 20) gets no chase and
        // no Move, like a variety with MovementSpeed 0; the small orb, whose type is not immobile, is chased with.
        var orbs = new MonsterCatalog();
        foreach (var (type, walks) in new[] { ("Metadata/Monsters/MavenBoss/TheMavenMapBeamOrb", false), ("Metadata/Monsters/MavenBoss/TheMavenMapBeamOrbSmall", true) })
        {
            var arena = Connection(lockstep: true);
            arena.LockstepFrame = (1.023, 1.056);
            arena.Monsters[0x3010] = new SpawnedMonster("orb", new PlayerWalk(130, 100, 20), aggroRange: 60, stopRange: 2, maxLife: 23)
            {
                Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Aggro = true, Variety = orbs.Find(type),
            };
            var orders = Game.PlanMonsterAi(arena, new MonsterAiCadence(), now: 1.03);
            Assert.Equal(walks, orders.Chases.Count + orders.Moves.Count + orders.MoveLegs.Count + orders.Legs.Count > 0);
        }
        // The recovery cancels the walk with the client's own stop, which waits for the tick (0x141e38420, 0x141e35a50):
        // a walker hit in the frame from 1.0 to 1.033 still takes its step due at 1.025 and stands a cell further on, with
        // the 8 ms since that step kept for its next walk. Without frames it stands where the hit finds it. The frame is
        // the one the 0x1d1 is written in: a hit committed a frame earlier (a projectile's, whose output follows its
        // commit) leaves the walker walking until then, as the client's does.
        foreach (bool frames in new[] { true, false })
        {
            (double Start, double End) sending = (0.967, 1.0);
            var walk = frames ? new PlayerWalk(100, 100, 8) { FrameOf = at => (Math.Min(at, sending.Start), sending.End) } : new PlayerWalk(100, 100, 8);
            walk.Order(110, 100, now: 0.9);
            var struck = new SpawnedMonster("Drowned", walk, aggroRange: 60, stopRange: 2, maxLife: 23) { HitAnimations = [new(0, 1.0)] };
            Assert.True(HitReaction.TryFlinch(struck, new(100), 0.999, new Random(1)));
            Assert.Equal(frames, walk.Moving(1.001));
            hurtConn.LockstepFrame = sending = (1.0, 1.033);
            lock (hurtConn.Monsters) Game.TakeHitPacket(hurtConn, 0x3002, struck, struck.FlinchUntil, 1.001);   // its send gate
            sending = (1.033, 1.066);
            Assert.Equal(frames ? (101, 100) : (100, 100), walk.Position(1.04));
            Assert.False(walk.Moving(1.04));
            walk.Order(110, 100, now: 1.04);
            Assert.Equal(frames ? 1.033 + 0.125 - 0.008 : 1.04 + 0.125, walk.Steps(1.04, 2)[0].At, 3);
        }
        // A Rampage stun's 0x1d1 is held in the same frames, and the stun's hold on the AI with it: 0.35 s from 10.0,
        // written at 10.001 in the frame from 9.990 with 0.349 s left, ends in the frame 11 later.
        var stunned = Drowned(new PlayerWalk(93, 102, 8));
        stunned.FlinchUntil = stunned.BusyUntil = 10.35;
        hurtConn.LockstepFrame = (9.990, 10.023);
        lock (hurtConn.Monsters)
            Assert.NotNull(Game.RampageStunPacket(hurtConn, new(0x3001, stunned, 1, stunned.ActionGeneration, 0, 0, 0, 10.35), 10.001));
        Assert.Equal(9.990 + 12 * 0.033, stunned.FlinchUntil, 9);
        Assert.Equal(stunned.FlinchUntil, stunned.BusyUntil);
        // A frame end overtakes the next chase (the AI pass and the frame loop are separate tasks): in that frame the
        // client still runs the walk-up, which steps into reach at 0.25 and swings. The chase is not sent into the swing,
        // and the next pass takes it from that frame's start.
        var overtaken = Connection(lockstep: true);
        overtaken.LockstepFrame = (0.231, 0.264);
        var racing = Chasing(Drowned(new PlayerWalk(110, 100, 8)));
        racing.Walk.Order(104, 100, now: 0);
        overtaken.Monsters[0x300a] = racing;
        plan = Game.PlanMonsterAi(overtaken, new MonsterAiCadence(), now: 0.235);
        var (_, _, chase, chaseFrom, chaseTo) = Assert.Single(plan.Chases);
        overtaken.LockstepFrame = (0.264, 0.297);
        lock (overtaken.Monsters) Assert.Null(Game.ChaseAtGate(overtaken, plan, 0x300a, racing, chase, chaseFrom, chaseTo));
        Assert.Equal((108, 100), racing.Walk.Position(0.3));
        plan = Game.PlanMonsterAi(overtaken, new MonsterAiCadence(), now: 0.27);
        Assert.Equal(0.231, Assert.Single(plan.ArrivedSwings).BegunAt, 3);

        // The player walks into its reach: his step to (105, 100) at 0.541 puts the Drowned at (100, 106) 7.8 cells off, in
        // the frame from 0.528, though outside the 2 cells inside the reach the server once kept for a walking player. It
        // swings, and its step due at 0.625 is not taken.
        var walkIn = Connection(lockstep: true);
        walkIn.LockstepFrame = (0.561, 0.594);
        walkIn.Walk.Teleport(125, 100, 0);
        walkIn.Walk.Order(60, 100, 0);
        var crossing = Chasing(Drowned(new PlayerWalk(100, 110, 8)));
        crossing.Walk.Order(100, 90, now: 0);
        walkIn.Monsters[0x3002] = crossing;
        plan = Game.PlanMonsterAi(walkIn, new MonsterAiCadence(), now: 0.57);
        Assert.Equal(0.528, Assert.Single(plan.ArrivedSwings).BegunAt, 3);
        Assert.Empty(plan.Swings);
        Assert.Equal((100, 106), crossing.Walk.Position(0.7));

        // At its path's end out of reach (the player went on to (100, 60)), the walk-up searches again toward his cell, in
        // the same frame. A Move that sets LastTarget (the King's, a Rampage chase) is no walk-up: it walks on past the
        // player within reach.
        var gone = Connection(lockstep: true);
        gone.LockstepFrame = (3.003, 3.036);
        gone.Walk.Order(100, 60, 0);
        var late = Chasing(Drowned(new PlayerWalk(130, 100, 8)));
        var walkUp = late.Walk.Approach(100, 100, 7, 0, tolerance: 1);
        Assert.Equal(((108, 100), 2.75), (walkUp.Stop, walkUp.At));
        var mover = Drowned(new PlayerWalk(106, 75, 8));
        mover.Walk.Order(106, 40, now: 1.5);   // within reach of the player's cell from (106, 65) at 2.75
        mover.LastTarget = (106, 40);
        gone.Monsters[0x3003] = late;
        gone.Monsters[0x3008] = mover;
        lock (gone.Monsters) Game.StepWalkers(gone, 3.01);
        Assert.True(late.Walk.Moving(3.003));
        Assert.InRange(Math.Sqrt(Math.Pow(late.Walk.Destination.X - 100, 2) + Math.Pow(late.Walk.Destination.Y - 60, 2)), 0, 8);
        Assert.Equal(late.ActionGeneration, late.ClientChase);
        Assert.Equal((106, 40), mover.Walk.Destination);
        Assert.True(mover.Walk.Moving(3.003));

        // A ranged monster's in-place attack leaves its Move running to the end, on a lockstep client as on a predictive
        // one: the estimate keeps its walk.
        SpawnedMonster Spitter(PlayerWalk walk) => new("Spitter", walk, aggroRange: 60, stopRange: 2, maxLife: 10)
        {
            Attack = new(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true }, Aggro = true, LastTarget = (120, 100),
        };
        var ranged = Connection(lockstep: true);
        var shooter = Spitter(new PlayerWalk(130, 100, 8));
        shooter.Walk.Order(120, 100, now: 6);
        ranged.Monsters[0x3004] = shooter;
        plan = Game.PlanMonsterAi(ranged, new MonsterAiCadence(), now: 6.5);
        Assert.Equal([0x3004u], plan.Swings.Select(s => s.Id));
        Assert.True(shooter.Walk.Moving(6.6));
        // Its order starts at the start of the frame it goes out in, from where the steps before it leave it. Written after
        // the 0x2b7 that ended the frame from 0.099 and before that frame's steps were replayed: the Drowned's step at
        // 0.105 blocks the Spitter's to (50, 55) at 0.100, so it shoots from (50, 56), busy from 0.132. A freeze sent
        // after the frame's start and before the order cancels it; a predictive client's starts when written.
        var shooting = Connection(lockstep: true);
        shooting.LockstepFrame = (0.132, 0.165);
        shooting.WalkersSteppedTo = 0.099;
        var blocker = Drowned(new PlayerWalk(50, 50, 1 / 0.105, size: 2));
        blocker.Walk.Order(50, 70, now: 0);
        var spitter = Spitter(new PlayerWalk(50, 56, 10, size: 2));
        spitter.Walk.Order(50, 36, now: 0);
        shooting.Monsters[0x3000] = blocker;
        shooting.Monsters[0x3001] = spitter;
        lock (shooting.Monsters)
        {
            spitter.Sequence = 0x8070;
            var shot = Game.MonsterSwingOrderLocked(shooting, spitter, spitter.Attack!, 7, generic: true, 0, 0.14);
            Assert.Equal(0x8071, spitter.Sequence);
            Assert.Equal(0x8071, shot!.Value.Sequence);
            Assert.Equal((0.132, 1.0, (50, 56)), (Math.Round(shot!.Value.StartedAt, 9), shot.Value.Seconds, shot.Value.From));
            Assert.Equal(0.132 + 1.0 + 0.066, spitter.BusyUntil, 9);
            Assert.NotNull(spitter.Ailments.Freeze(2, 0.15, _ => 0, _ => 0));
            Assert.Null(Game.MonsterSwingOrderLocked(shooting, spitter, spitter.Attack!, 8, generic: true, 0, 0.16));
            Assert.Equal(0x8071, spitter.Sequence);
        }
        Assert.Equal(0.14, Game.LockstepOrderStart(Connection(lockstep: false), 0.14));

        // Scripted dashes and ordinary casts belong to one actor. A cast chooses its sequence only at admission;
        // asking for a sequence before that must not spend one, or interleaved King actions go backwards or skip.
        var scripted = Connection(lockstep: true);
        var king = Drowned(new PlayerWalk(100, 100, 8));
        king.Sequence = 0xfffd;
        Assert.Equal((ushort)0xfffe, KingInMists.SequenceForAction(scripted, king, swing: false));
        Assert.Null(KingInMists.SequenceForAction(scripted, king, swing: true));
        Assert.Equal((ushort)0xfffe, king.Sequence);
        var cast = Game.MonsterSwingOrderLocked(scripted, king, king.Attack!, 1, generic: false, 1, 0);
        Assert.Equal((ushort)0xffff, cast!.Value.Sequence);
        Assert.Equal((ushort)0x8000, KingInMists.SequenceForAction(scripted, king, swing: false));
        var scriptedPredictive = Connection(lockstep: false);
        var predictiveKing = Drowned(new PlayerWalk(100, 100, 8));
        ushort predictiveSequence = predictiveKing.Sequence;
        ushort direct = KingInMists.SequenceForAction(scriptedPredictive, predictiveKing, swing: false)!.Value;
        ushort explicitCast = KingInMists.SequenceForAction(scriptedPredictive, predictiveKing, swing: true)!.Value;
        Assert.Equal((ushort)(direct + 1), explicitCast);
        Assert.Equal(predictiveSequence, predictiveKing.Sequence);

        // A scripted dash's first pair is the actor's origin, then its aim. The native action start places the actor
        // on that origin (always in lockstep; beyond the correction threshold in predictive), even with a target id.
        // Keep the object header, player target, skill and sequence while encoding the two distinct cells in order.
        Assert.Equal(new byte[]
        {
            0x01, 0xab, 0x08, 0, 0, 1, 0, 0, 0, 0, 0x70, 0x01, 0, 0x80, 0, 0, 0x10, 0,
            0x80, 0xf3, 0x82, 0x63, 0x80, 0xfd, 0x82, 0x72, 0x40, 0x0c, 0x80, 0x05,
        }, KingInMists.MovementAction(0x08000001, (243, 611), (253, 626), 0x400c, 0x8005, Program.PlayerId, 0x7001));

        var predictive = Connection(lockstep: false);
        var monster = Drowned(new PlayerWalk(104, 100, 8));
        predictive.Monsters[0x3000] = monster;
        var walker = Spitter(new PlayerWalk(130, 100, 8));
        walker.Walk.Order(120, 100, now: 0);
        predictive.Monsters[0x3004] = walker;
        plan = Game.PlanMonsterAi(predictive, new MonsterAiCadence(), now: 1);
        Assert.Empty(plan.ArrivedSwings);
        Assert.Equal([0x3000u, 0x3004u], plan.Swings.Select(s => s.Id).Order());
        Assert.True(walker.Walk.Moving(1.1));
    }

    // A melee chase farther than a leg past the reach goes as a Move to the cell a leg along its walk-up, placed by the
    // plan (the client's one search budget per frame, 07 §5); within a leg of the reach the attack aimed at the player
    // walks up as before.
    [Fact]
    public void A_far_melee_chase_goes_in_legs_and_a_near_one_as_the_attack_walk_up()
    {
        var conn = Connection(lockstep: false);
        var far = Drowned(new PlayerWalk(160, 100, 8));    // 60 cells from the player, reach 8: beyond a leg of the reach
        var near = Drowned(new PlayerWalk(115, 100, 8));   // 15 cells: within it
        conn.Monsters[0x3000] = far;
        conn.Monsters[0x3001] = near;
        var plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 1);
        var leg = Assert.Single(plan.Legs);
        Assert.Equal(0x3000u, leg.Id);
        Assert.Equal((160, 100), leg.From);
        Assert.Equal((148, 100), leg.To);   // 12 cells along the walk-up, at 8 cells a second
        Assert.Equal((148, 100), far.Walk.Destination);
        Assert.True(far.Walk.Moving(1.5));
        Assert.Equal((1.125, (159, 100)), far.Walk.Steps(1, 2)[0]);   // from a stand, a whole step after the order
        Assert.Null(far.LastTarget);
        Assert.Equal([0x3001u], plan.Chases.Select(c => c.Id));
        Assert.DoesNotContain(0x3000u, plan.Chases.Select(c => c.Id));

        // A ranged chaser's Move to its surround slot is cut the same way while the slot is more than a leg away.
        var shooter = new SpawnedMonster("Spitter", new PlayerWalk(190, 100, 8), aggroRange: 60, stopRange: 2, maxLife: 10)
        {
            Attack = new(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true }, Aggro = true,
        };
        conn.Monsters[0x3002] = shooter;
        plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 2);
        var moveLeg = Assert.Single(plan.MoveLegs);
        Assert.Equal(0x3002u, moveLeg.Id);
        Assert.Equal((190, 100), moveLeg.From);
        Assert.InRange(Math.Sqrt(Math.Pow(moveLeg.To.X - 190, 2) + Math.Pow(moveLeg.To.Y - 100, 2)), 11, 13);
        Assert.Equal(moveLeg.To, shooter.Walk.Destination);
        Assert.Equal(moveLeg.To, shooter.Leg);
        Assert.Empty(plan.Moves);

        // Out of the wake zone (asleep), a monster gets no order at all: the client's copy stands frozen out there.
        var asleep = Drowned(new PlayerWalk(170, 100, 8));
        asleep.Asleep = true;
        conn.Monsters[0x3003] = asleep;
        plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 3);
        Assert.DoesNotContain(0x3003u, plan.Legs.Select(l => l.Id).Concat(plan.Chases.Select(c => c.Id)).Concat(plan.MoveLegs.Select(l => l.Id)));
        Assert.False(asleep.Walk.Moving(3.5));

        // Predictive uses its continuous clock, not another connection's lockstep frame. Its stop still keeps elapsed
        // step time: 0x141e38420/0x141e35a50/0x141e50880 share this rule in both modes. The next leg starts with that
        // time unscaled, including when the temporary walk used to select the leg has a different step duration.
        var served = Connection(lockstep: false);
        SpawnedMonster Spitter(uint id, int x, int y) => new("Spitter", served.NewMonsterWalk(id, x, y, 8, 2, false), aggroRange: 120,
            stopRange: 2, maxLife: 10)
        {
            Attack = new(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true }, Aggro = true,
        };
        var walking = Spitter(0x3005, 190, 60);
        walking.Walk.Order(190, 20, now: 1.5);     // north, a step every 0.125 s: (190, 56) at 2.0
        var stopped = Spitter(0x3006, 190, 140);
        served.LockstepFrame = (1.947, 1.98);
        stopped.Walk.Order(230, 140, now: 1.5);    // east: (193, 140) at 1.875
        stopped.Walk.Order(193, 140, now: 1.96);   // stopped 0.085 s into its next step
        served.Monsters[0x3005] = walking;
        served.Monsters[0x3006] = stopped;
        served.LockstepFrame = (2.013, 2.046);
        var legs = Game.PlanMonsterAi(served, new MonsterAiCadence(), now: 2.03).MoveLegs;
        Assert.Equal([0x3005u, 0x3006u], legs.Select(l => l.Id));
        Assert.Equal(((190, 56), (193, 140)), (legs[0].From, legs[1].From));
        var steps = walking.Walk.Steps(2.03, 3);
        Assert.Equal(2.03 + (1 - 0.24) * (steps[1].At - steps[0].At), steps[0].At, 9);   // 0.03 s into a 0.125 s step
        steps = stopped.Walk.Steps(2.03, 3);
        Assert.Equal(2.03 - 0.085 + (steps[1].At - steps[0].At), steps[0].At, 9);   // predictive stop at1.96; not the lockstep frame end1.98
        Assert.Equal((193, 140), stopped.Walk.Position(2.03));
        Assert.Equal((legs[0].To, legs[1].To), (walking.Walk.Destination, stopped.Walk.Destination));

        // A diagonal stop can keep more time than the next horizontal step needs. Its due step belongs after the new
        // order, where the collision pass can see it, never retroactively before the order or in its reported origin.
        var due = new PlayerWalk(100, 100, 8);
        due.Order(120, 120, 0);
        due.Order(100, 100, 0.149);
        Assert.Equal((100, 100), due.Order(120, 100, 1));
        Assert.Equal((100, 100), due.Position(1));
        var resumed = due.Steps(1, 1.05);
        Assert.Single(resumed);
        Assert.Equal((101, 100), resumed[0].Cell);
        Assert.True(resumed[0].At > 1);
        Assert.Equal(1.101, due.Steps(1.05, 1.2).Single().At, 9);
    }

    // On a lockstep client a walker's position reads as of the current frame's start, so the leg's end must be read
    // along the walk itself: read as a position it was the walker's own cell, and every far melee chase went out as a
    // Move onto its own cell (live 2026-11-04: all 3 303 leg orders of two fights; the melee monsters stood). An
    // estimate that finds no way, standing or walking, keeps the attack aimed at the player, whose action then ends with
    // no walk-up.
    [Fact]
    public void A_lockstep_leg_placed_at_the_gate_goes_a_leg_along_or_keeps_the_attack()
    {
        var conn = Connection(lockstep: true);
        conn.LockstepFrame = (1.0, 1.033);
        var far = Drowned(new PlayerWalk(160, 100, 8) { FrameOf = at => (Math.Min(at, 1.0), 1.033) });
        var (here, aim, attack) = Game.PlaceLockstepLeg(conn, far, 1, 1.0);
        Assert.False(attack);
        Assert.Equal((160, 100), here);
        Assert.Equal((148, 100), aim);   // 12 cells along the walk-up, at 8 cells a second
        Assert.Equal((148, 100), far.Walk.Destination);
        Assert.Null(far.ClientChase);

        var walled = Drowned(new PlayerWalk(160, 100, 8, walkable: (x, y) => (x, y) == (160, 100)) { FrameOf = at => (Math.Min(at, 1.0), 1.033) });
        (here, aim, attack) = Game.PlaceLockstepLeg(conn, walled, 1, 1.0);
        Assert.True(attack);
        Assert.Equal((160, 100), here);
        Assert.Equal((100, 100), aim);   // the player's cell
        Assert.Null(walled.LastTarget);
        Assert.Null(walled.ClientChase);
        // Walled in while it walks: the stop waits for the frame's steps, and the action ends all the same.
        bool walls = false;
        var cutOff = Drowned(new PlayerWalk(160, 100, 8, walkable: (x, y) => !walls || (x, y) == (160, 100)) { FrameOf = at => (Math.Min(at, 1.0), 1.033) });
        cutOff.Walk.Order(150, 100, now: 0.9);
        walls = true;
        Assert.True(Game.PlaceLockstepLeg(conn, cutOff, 1, 1.0).Attack);
        Assert.Null(cutOff.LastTarget);
        Assert.Null(cutOff.ClientChase);

        // A monster a stop left 149 ms into a 177 ms diagonal step starts its leg from the stand with that time, as the
        // client does with the one order it gets: a step in the order's frame. The walk-up placed to find the leg, and the
        // Move a leg is cut from, never ran and must not spend it.
        double start = 1.0;
        SpawnedMonster Stood()
        {
            start = 1.0;
            var m = Drowned(new PlayerWalk(161, 101, 8) { FrameOf = at => (Math.Min(at, start), start + .033) });
            m.Walk.Order(150, 90, now: 0);
            m.Walk.Order(156, 96, now: 1.0);   // stands at (156, 96) after the frame
            start = 1.099;
            return m;
        }
        conn.LockstepFrame = (1.099, 1.132);
        var stood = Stood();
        Assert.False(Game.PlaceLockstepLeg(conn, stood, 1, 1.099).Attack);
        Assert.Single(stood.Walk.Steps(1.099, 1.132));
        stood = Stood();
        Assert.True(Game.PlaceLockstepMoveLeg(stood, (120, 96), 1.099).Taken);
        Assert.Single(stood.Walk.Steps(1.099, 1.132));
    }

    // A lockstep client takes no Move whose target is within two cells of the walker (0x141949a20): no set, no search,
    // its current action goes on (live 2026-11-04: 317 of 317). So such a surround-slot or other plan Move is not sent,
    // a give-up or a freeze sends nothing, a minion that near its approach cell attacks instead, and the estimate keeps
    // the walk it had, a chase's walk-up too; three cells away the Move is placed as before and ends the walk-up, with
    // GGG's monster flags 0 (a predictive client's keeps 0x0408).
    [Fact]
    public async Task A_lockstep_move_within_two_cells_is_not_sent_and_the_walk_goes_on()
    {
        PlayerWalk Walker() => new(100, 100, 8) { FrameOf = at => (Math.Min(at, 1.0), 1.033) };
        var shooter = new SpawnedMonster("Spitter", Walker(), aggroRange: 60, stopRange: 2, maxLife: 10);
        shooter.Walk.Order(120, 100, now: 0.5);   // the slot it was walking to: 4 cells along at 1.0
        var (here, aim, taken) = Game.PlaceLockstepMoveLeg(shooter, (106, 100), 1.0);
        Assert.False(taken);
        Assert.Equal((104, 100), here);
        Assert.Equal((106, 100), aim);
        Assert.Equal((120, 100), shooter.Walk.Destination);   // the old walk goes on, as on the client
        Assert.True(shooter.Walk.Moving(1.5));

        (here, aim, taken) = Game.PlaceLockstepMoveLeg(shooter, (107, 100), 1.0);
        Assert.True(taken);
        Assert.Equal((107, 100), aim);
        Assert.Equal((107, 100), shooter.Walk.Destination);
        // The client's sum of squares is a 32-bit int read as unsigned (0x1400f0c90): one past int's top is far, one that
        // wraps to 0 is not (a player's target is its cell plus the 0x18e's i32 offsets, unclamped).
        Assert.True(Game.LockstepTakesMove((0, 0), (46341, 0)));
        Assert.False(Game.LockstepTakesMove((0, 0), (65536, 0)));

        // A give-up gets no order, as GGG's gets none: the estimate walks on to its old target, as the client's monster did
        // after a dropped give-up Move. A frozen walker gets no stop. A predictive client gets both Moves.
        (SpawnedMonster Leaving, SpawnedMonster Frozen) Pair(ClientState on, bool frames)
        {
            PlayerWalk At(int x) => frames ? new(x, 100, 8) { FrameOf = at => (Math.Min(at, 1.0), 1.033) } : new(x, 100, 8);
            var leaving = Drowned(At(230));
            leaving.Target = on;   // An existing pursuit belongs to this live player before it crosses the give-up distance.
            leaving.Walk.Order(218, 100, now: 0.5);   // at (226, 100) at 1.0: 126 cells from the player, past twice its aggro range
            var frozen = Drowned(At(160));
            frozen.Walk.Order(140, 100, now: 0.5);   // at (156, 100) at 1.0, in its aggro range
            Assert.NotNull(frozen.Ailments.Freeze(2, 0.9, _ => 0, _ => 0));
            Assert.Equal(0d, Game.MonsterActionFactor(frozen, 1.0));   // the freeze took
            (on.Monsters[0x3000], on.Monsters[0x3001]) = (leaving, frozen);
            return (leaving, frozen);
        }
        var held = Connection(lockstep: true);
        held.LockstepFrame = (1.0, 1.033);
        var (left, frozenWalker) = Pair(held, frames: true);
        (left.ClientChase, frozenWalker.ClientChase) = (left.ActionGeneration, frozenWalker.ActionGeneration);   // walk-ups
        // whose searches set up those walks: their search timers run from then (0x141e826a0)
        (left.ClientChaseTimerFrom, frozenWalker.ClientChaseTimerFrom) = (0.5, 0.5);
        var plan = Game.PlanMonsterAi(held, new MonsterAiCadence(), now: 1.0);
        Assert.Empty(plan.EndedActions);
        Assert.Empty(plan.Moves);
        Assert.Equal((218, 100), left.Walk.Destination);
        Assert.True(left.Walk.Moving(1.5));
        Assert.Equal((140, 100), frozenWalker.Walk.Destination);
        Assert.True(Game.WalkUpOpen(held, left));   // nothing reached the client: its action goes on
        Assert.True(Game.WalkUpOpen(held, frozenWalker));
        // Any other plan Move (a flight, a minion's or a Rampage chase) is placed at the send gate only when the client
        // takes it: 2 cells from (226, 100) it is not sent, 3 cells away it goes out and ends the walk-up.
        lock (held.Monsters)
        {
            // An admitted order belongs to this actor, even when other actors have sent more orders.
            // Refusal spends no sequence; the accepted Move follows this actor's last chase.
            left.Sequence = 0x8050;
            held.NextActionSequence();
            held.NextActionSequence();
            Assert.Null(Game.PlanMoveAtGate(held, plan, 0x3000, left, Game.NextPlannedAction(left), (226, 100), (228, 100), "flees"));
            Assert.Equal(0x8050, left.Sequence);
            Assert.Equal((218, 100), left.Walk.Destination);
            Assert.True(Game.WalkUpOpen(held, left));
            var sent = Game.PlanMoveAtGate(held, plan, 0x3000, left, Game.NextPlannedAction(left), (226, 100), (229, 100), "flees");
            Assert.NotNull(sent);
            Assert.Equal(0x8051, BinaryPrimitives.ReadUInt16BigEndian(sent.AsSpan(sent.Length - 2)));
            Assert.Equal(0x8051, left.Sequence);
            Assert.Equal((229, 100), left.Walk.Destination);
            Assert.False(Game.WalkUpOpen(held, left));
            // A monster that left the wake zone after the plan gets no order: its copy no longer ticks, and the estimate
            // stays where the leave left it.
            held.AsleepMonsters.Add(0x3000);
            Assert.Null(Game.PlanMoveAtGate(held, plan, 0x3000, left, Game.NextPlannedAction(left), (226, 100), (240, 100), "flees"));
            Assert.Equal(0x8051, left.Sequence);
            Assert.Equal((229, 100), left.Walk.Destination);
            held.AsleepMonsters.Remove(0x3000);
            // It carries GGG's monster flags, 0 (none of its 56 175 monster orders has 0x400 or 0x8): without 0x400 the
            // client refuses it while a non-Move action waits queued (0x141e937b0), as GGG's client does.
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(sent.AsSpan(12)));
        }
        var predictive = Connection(lockstep: false);
        var (gone, stopped) = Pair(predictive, frames: false);
        var relayPlan = Game.PlanMonsterAi(predictive, new MonsterAiCadence(), now: 1.0);
        Assert.Equal(new uint[] { 0x3000 }, relayPlan.EndedActions);
        var moves = relayPlan.Moves;
        Assert.Equal([(0x3000u, "gives up"), (0x3001u, "is frozen")], moves.Select(mv => (mv.Id, mv.Why)).Order());
        Assert.Equal(moves.Single(mv => mv.Id == 0x3000u).To, gone.Walk.Destination);
        Assert.False(stopped.Walk.Moving(1.1));
        // A predictive client's monster Moves keep 0x0408.
        lock (predictive.Monsters)
        {
            var relayed = Game.PlanMoveAtGate(predictive, relayPlan, 0x3000, gone, Game.NextPlannedAction(gone),
                (226, 100), (229, 100), "flees");
            Assert.Equal(0x0408, BinaryPrimitives.ReadUInt16BigEndian(relayed.AsSpan(12)));
        }

        // A fighting minion 2 cells from its approach cell gets its attack aimed at the target instead of that Move: a melee
        // one walks up on the client (0x141e9afc0) and swings at the walk-up's end, an in-place one shoots from where it
        // stands. A predictive client gets the Moves.
        (SpawnedMonster Melee, SpawnedMonster Archer) Fighters(ClientState on, bool frames)
        {
            PlayerWalk At(int y) => frames ? new(100, y, 8) { FrameOf = at => (Math.Min(at, 1.0), 1.033) } : new(100, y, 8);
            on.Monsters[0x3010] = Drowned(At(130));   // the target: its size 2 and their reach 8 make 10, the approach 9 off
            var melee = new SpawnedMonster("Chieftain", At(141), aggroRange: 60, stopRange: 2, maxLife: 100)
            {
                Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Hostile = false,
            };
            var shooter = new SpawnedMonster("Ahuana", At(119), aggroRange: 60, stopRange: 2, maxLife: 100)
            {
                Attack = new(0x1535, 1.0, 8, DamagePacket.Empty) { Ranged = true }, Hostile = false,
            };
            (on.Monsters[0x3011], on.Monsters[0x3012]) = (melee, shooter);
            on.Minions.Add(new(0x3011, melee, "test", "melee") { Fights = true });
            on.Minions.Add(new(0x3012, shooter, "test", "ranged") { Fights = true });
            return (melee, shooter);
        }
        var fighting = Connection(lockstep: true);
        fighting.LockstepFrame = (1.0, 1.033);
        var (melee, archer) = Fighters(fighting, frames: true);
        plan = Game.PlanMonsterAi(fighting, new MonsterAiCadence(), now: 1.0);
        Assert.Empty(plan.Moves);
        var (walkUp, shot) = (plan.MinionSwings.Single(a => a.Id == 0x3011), plan.MinionSwings.Single(a => a.Id == 0x3012));
        Assert.Equal((100, 139), melee.Walk.Destination);
        Assert.True(melee.Walk.ArrivesAt > 1.2);
        Assert.Equal(melee.Walk.ArrivesAt + 1.0, walkUp.ContactAt, 6);
        Assert.Equal(melee.Walk.ArrivesAt + 2.0, melee.BusyUntil, 6);
        Assert.Equal((100, 119), archer.Walk.Destination);
        Assert.False(archer.Walk.Moving(1.1));
        Assert.Equal(1.5, shot.ContactAt, 6);
        var told = Connection(lockstep: false);
        Fighters(told, frames: false);
        plan = Game.PlanMonsterAi(told, new MonsterAiCadence(), now: 1.0);
        Assert.Empty(plan.MinionSwings);
        Assert.Equal([(0x3011u, (100, 139)), (0x3012u, (100, 121))], plan.Moves.Select(mv => (mv.Id, mv.To)).Order());

        // The player's Moves follow the same rule (live 2026-11-04: 308 of 308): a held cursor 2 cells from the walking
        // player starts nothing and the walk goes on; 3 cells away the Move is placed.
        var conn = Connection(lockstep: true, character: "LockstepNearMove");
        conn.LockstepFrame = (1.0, 1.033);
        conn.Walk.Order(140, 100, 0.5);
        var player = conn.Walk.Position(1.0);
        Assert.Null(Game.PlaceLockstepPlayerMove(conn, (player.X + 2, player.Y), 1.0));
        Assert.Equal((140, 100), conn.Walk.Destination);
        Assert.True(conn.Walk.Moving(1.1));
        // The frame pass spends nothing on it either: no sequence, no action, no 0x1ab (none can go out here). The pass
        // reads the character's gems, so it gets a save store of its own.
        conn.LockstepLatency = new();
        conn.LockstepHold.Press((player.X + 2, player.Y), 0x2909, 0x0408);
        var spent = (conn.LockstepSequence, conn.ActionCount);
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-lockstep-near-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        try
        {
            await Game.StartLockstepActions("near", null!, null!, conn, 1.0);
            Assert.Single(conn.LockstepTrail);   // the pass ran past the latency gate
            // Projectile time can be ahead of the current client frame. A pickup's 100 ms hold must still
            // finish on that frame clock before a pending Move is spent, or its path is lost on completion.
            conn.Clock.Reset();
            var session = new Game.ProjectileSession("pickup-frame", null!, null!, conn);
            conn.ProjectileRuntimeState = session;
            try
            {
                session.AdvanceTo(ProjectileRuntime.Time(1.105));
                conn.LockstepBusyUntil = 1.1;
                conn.LockstepFrame = (1.099, 1.132);
                conn.LockstepHold.Press((160, 100), 0x2909, 0);
                await Game.StartLockstepActions("pickup-frame", null!, null!, conn, 1.099);
                Assert.Equal(spent, (conn.LockstepSequence, conn.ActionCount));
                Assert.Equal((140, 100), conn.Walk.Destination);
                Assert.Equal(ProjectileRuntime.Time(1.105), session.Runtime.Now);
            }
            finally { session.Invalidate(); conn.ProjectileRuntimeState = null; }
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            TestEnvironment.ReleaseSaves(saves);
        }
        Assert.Equal(spent, (conn.LockstepSequence, conn.ActionCount));
        Assert.Equal((140, 100), conn.Walk.Destination);
        Assert.True(conn.Walk.Moving(1.1));
        Assert.Equal(player, Game.PlaceLockstepPlayerMove(conn, (player.X + 3, player.Y), 1.0));
        Assert.Equal((player.X + 3, player.Y), conn.Walk.Destination);
    }

    // A mortar attack is the client's own in both modes: its action fires only with a count on its skill record
    // (0x141ba7c30) and draws the MonsterMortar row skill_behaviour_variation names (0x141b6c390; none is row 0,
    // MortarMolten), as on GGG's Spitter records. The count is the skill's, the same on the record and in the server's
    // flight, which lands the hit but creates no object; a straight shot (the King's BugSoulrend) has no row, no count on
    // its record and keeps its object.
    [Fact]
    public async Task A_mortar_skill_record_carries_the_client_s_count_and_row_and_the_server_draws_no_mortar()
    {
        var skills = new SkillCatalog("data");
        foreach (bool lockstep in new[] { true, false })
        {
            var conn = new ClientState(new(1, "mortar-goal", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500, PlayerSent = true,
                LockstepClient = lockstep, Instance = new(),
            };
            // Exercise the real create path: the selected granted record and the AI must describe the same action.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var reader = await accepting;
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            string database = Path.Combine(Path.GetTempPath(), $"localexile-mortar-goal-{Guid.NewGuid():N}.db");
            Saves.Open(database);
            try
            {
                conn.Wire = ("mortar-goal", writer.GetStream(), ciphers);
                var nativeVariety = new MonsterCatalog().Find("Metadata/Monsters/SandSpitters/SandSpitter")!;
                uint id = await Game.SpawnMonster("mortar-goal", writer.GetStream(), ciphers, conn, nativeVariety,
                    70, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 140, 100,
                    log: false, mods: MonsterModSet.None, unrisen: false);
                // Creates belong to the frame tail (0x14218a650), after established objects have ticked.
                int typeLength = GameServer.Core.Protocol.Instance.InstanceEntities.MonsterTypes([((ushort)nativeVariety.Row, (byte)70)]).Length;
                var createHeader = new byte[typeLength + 12];
                await reader.GetStream().ReadExactlyAsync(createHeader);
                new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(createHeader);
                Assert.Equal(0x2bb, BinaryPrimitives.ReadUInt16BigEndian(createHeader.AsSpan(typeLength)));
                Assert.Equal(lockstep ? uint.MaxValue : 0u,
                    BinaryPrimitives.ReadUInt32BigEndian(createHeader.AsSpan(typeLength + 6)));
            }
            finally { TestEnvironment.ReleaseSaves(database); }
            foreach (var (effectId, row, count) in new[] { ("MMASandSpitterProjectile", 183, 1), ("MMASandSpitterProjectileLightning", 186, 1),
                ("ScavengerThrowFire", 55, 1), ("MMASandSpitterProjectileSideArea", 183, 3) })
            {
                int effect = skills.EffectRow(effectId);
                var mortar = skills.MonsterProjectile(effect);
                Assert.NotNull(mortar);
                Assert.Equal(row, mortar!.Variation);
                Assert.Equal(count, mortar.Count);
                var stats = Game.MonsterProjectileSkillStats(conn, effect, 70,
                    new MonsterAttack(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true, Projectile = mortar });
                int Stat(uint key) => Assert.Single(stats, s => s.Item1 == key).Item2;
                Assert.Equal(row, Stat(StatKeys.SkillBehaviourVariation));
                Assert.Equal(count, Stat(StatKeys.TotalProjectilesToFire));
                Assert.Equal(count, Stat(StatKeys.VirtualTotalProjectiles));
                Assert.Equal(count, Stat(StatKeys.VirtualTotalArrows));
                var profile = Game.MonsterProjectileProfile(mortar, 5, 10, 30);
                Assert.True(profile.HideVisual, effectId);
                Assert.Equal(count, profile.Count);
            }
            // A spell mortar (monster_mortar, a different client action, not traced) keeps the server's object.
            int spellEffect = skills.EffectRow("LegionVaalViperSummonSnakeProjectile");
            var spell = skills.MonsterProjectile(spellEffect);
            Assert.NotNull(spell);
            Assert.Equal((0, 5), (spell!.Variation, spell.Count));
            Assert.DoesNotContain(Game.MonsterProjectileSkillStats(conn, spellEffect, 70,
                    new MonsterAttack(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true, Projectile = spell }),
                s => s.Item1 is StatKeys.SkillBehaviourVariation or StatKeys.VirtualTotalProjectiles);
            Assert.False(Game.MonsterProjectileProfile(spell, 5, 10, 30).HideVisual);
            var bugs = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugSoulrend", 200, Lobbed: false, 3) { Count = 6 };
            var straight = Game.MonsterProjectileSkillStats(conn, skills.EffectRow("MMASandSpitterProjectile"), 70,
                new MonsterAttack(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true, Projectile = bugs });
            Assert.DoesNotContain(straight, s => s.Item1 is StatKeys.SkillBehaviourVariation or StatKeys.VirtualTotalProjectiles);
            Assert.False(Game.MonsterProjectileProfile(bugs, 5, 10, 30).HideVisual);
        }
        var spitter = skills.MonsterProjectile(skills.EffectRow("MMASandSpitterProjectile"))!;
        var variety = new MonsterCatalog().Find("Metadata/Monsters/SandSpitters/SandSpitter")!;
        var orderedSource = new SpawnedMonster("Spitter", new(100, 100, 20), 60, 2, 15) { Variety = variety, Level = 70 };
        var orderedAttack = new MonsterAttack(0x1535, 1, 30, DamagePacket.Empty)
            { Projectile = spitter, ReleaseEffect = skills.EffectRow("MMASandSpitterProjectile") };
        var selectedClips = new HashSet<string>();
        foreach (ushort sequence in new ushort[] { 0x8071, 0x8072, 0x8073, 0x8074 })
        {
            var clip = skills.MonsterContact(variety.Id, "SpellProjectile", sequence)!;
            var release = Game.MonsterReleaseFor(Connection(true), variety,
                skills.EffectRow("MMASandSpitterProjectile"), 70, spitter, sequence)!;
            Assert.Equal(clip.Animation, release.Clip);
            Assert.Equal(clip.ContactFraction, release.Fraction);
            Assert.Equal(release, Game.MonsterAttackForSequence(Connection(true), orderedSource, orderedAttack, sequence).Release);
            selectedClips.Add(release.Clip);
        }
        Assert.Equal(2, selectedClips.Count);
        Assert.Null(orderedAttack.Release); // Selecting an action never rewrites the reusable attack recipe.
        Assert.False(spitter.Lobbed);
        Assert.Equal((50, 3), (spitter.MinimumRange, spitter.SpreadRadius));
        // Spitter contact occurs on its twelfth 33 ms frame, not between frames at 0.371 s.
        Assert.Equal(10 + 12 * .033, Game.LockstepShotAt(10, .99 * .3745), 6);
        var direct = Game.MonsterProjectileProfile(spitter, 107.64, 11, 50) with { ContactCells = 2 };
        Assert.True(direct.DirectDamage);
        Assert.Null(direct.EndArea);
        // Native straight-path destinations for standing and moving targets (141b6c630).
        Assert.Equal(new ProjectileVector(848, 533), Game.MonsterMortarTarget(new(894, 554), new(890, 552),
            new(876, 546), 50, 3, 0xffff, 0x8000032, 0));
        Assert.Equal(new ProjectileVector(854, 583), Game.MonsterMortarTarget(new(894, 554), new(891, 557),
            new(877, 567), 50, 3, 0xffff, 0x8000032, 0));
        var aimed = Connection(lockstep: true);
        aimed.Clock.Stop();
        aimed.Walk.Teleport(880, 548, 0);
        var source = new SpawnedMonster("Spitter", new(894, 554, 20), 60, 2, 15);
        var attack = new MonsterAttack(0x1535, 1, 30, DamagePacket.Empty) { Projectile = spitter };
        var session = new Game.ProjectileSession("ordered-aim", null!, null!, aimed);
        try
        {
            var first = Game.MonsterProjectileUse(session, 0x8000032, source, attack, spitter, 0, orderedAim: (880, 548));
            aimed.Walk.Teleport(880, 564, 0);
            var dodged = Game.MonsterProjectileUse(session, 0x8000032, source, attack, spitter, 0, orderedAim: (880, 548));
            Assert.Equal(first.Origin, dodged.Origin);
            Assert.Equal(first.Aim, dodged.Aim);
            Assert.Equal(first.ProjectileTargets, dodged.ProjectileTargets);
            Assert.NotEqual(dodged.Aim, Game.MonsterProjectileUse(session, 0x8000032, source, attack, spitter, 0).Aim);
        }
        finally { session.Invalidate(); }
        foreach (bool dodge in new[] { false, true })
        {
            var world = new MortarContactWorld(dodge);
            var runtime = new ProjectileRuntime(world);
            runtime.Release(new(direct, new(2), new(2), 2, new(0, 0), new(20, 0))
                { ProjectileTargets = [new(50, 0)] });
            runtime.AdvanceTo(ProjectileRuntime.Time(1));
            Assert.Equal(dodge ? 0 : 1, world.Hits);
        }
    }

    [Fact]
    public void A_queued_boss_bar_cannot_reshow_a_corpse_a_hidden_phase_or_a_replaced_actor()
    {
        var conn = Connection(lockstep: true);
        var boss = new SpawnedMonster("Boss", new PlayerWalk(102, 100, 8), 60, 2, 10)
            { Variety = new(1, "boss", "Boss", "Monster", null, ["Positioned", "StateMachine"]) };
        boss.States = new([0, 0], 0, ["boss_life_bar", "aggro"]);
        boss.BossBarShown = true;
        const uint id = 0x3000;
        conn.Monsters[id] = boss;
        Assert.NotNull(Game.BossLifeBarRecord(conn, id, boss));
        boss.States = boss.States.Set("boss_life_bar", 0);
        boss.Life = 0;
        Assert.Null(Game.BossLifeBarRecord(conn, id, boss));
        Assert.Equal(0, boss.States.SharedStates[0]);
        boss.Life = 10;
        boss.BossBarShown = false;
        Assert.Null(Game.BossLifeBarRecord(conn, id, boss));
        boss.BossBarShown = true;
        conn.Monsters.Remove(id);
        Assert.Null(Game.BossLifeBarRecord(conn, id, boss));
    }

    [Fact]
    public async Task A_projectile_hit_keeps_its_action_sequence_after_the_monster_starts_another_action()
    {
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-shot-sequence-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        try
        {
            foreach (bool lockstep in new[] { false, true })
            {
                var conn = Connection(lockstep, $"ShotSequence{lockstep}");
                conn.Clock.Stop();
                conn.Walk.Teleport(880, 548, 0);
                var source = new SpawnedMonster("Spitter", new(894, 554, 20), 60, 2, 15) { Sequence = 0x8071 };
                var skills = new SkillCatalog();
                var spitter = skills.MonsterProjectile(skills.EffectRow("MMASandSpitterProjectile"))!;
                var attack = new MonsterAttack(0x1535, 1, 30, DamagePacket.Empty) { Projectile = spitter, Spell = true };
                var session = new Game.ProjectileSession("shot-sequence", null!, null!, conn);
                try
                {
                    var use = Game.MonsterProjectileUse(session, 0x8000032, source, attack, spitter, 0, sequence: 0x8071);
                    var runtime = new ProjectileRuntime(new MortarContactWorld(false));
                    runtime.Release(use);
                    source.NextSequence();
                    var contact = new ProjectileDamage(Assert.Single(runtime.Projectiles), new(0x1000),
                        ProjectileRuntime.Time(1), "direct", new(880, 548));
                    var outcome = Game.CommitHostilePlayerHitLocked(session, contact,
                        Assert.IsType<HostileProjectileOffense>(use.Payload));
                    Assert.NotNull(outcome);
                    Assert.Equal(0x1af, BinaryPrimitives.ReadUInt16BigEndian(outcome.HitPacket));
                    Assert.Equal(0x8071, BinaryPrimitives.ReadUInt16BigEndian(outcome.HitPacket.AsSpan(12)));
                }
                finally { session.Invalidate(); await session.OutputCompleted; }
            }
        }
        finally
        {
            PlayerInventories.Forget("ShotSequenceFalse");
            PlayerInventories.Forget("ShotSequenceTrue");
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    sealed class MortarContactWorld(bool dodge) : IProjectileWorld
    {
        public int Hits;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) =>
            [new(new(1), 1, t => new(20, dodge ? 40 * t / (double)ProjectileRuntime.QuantaPerSecond : 0), 0) { Size = 1 }];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) { Hits++; return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) { }
    }
}

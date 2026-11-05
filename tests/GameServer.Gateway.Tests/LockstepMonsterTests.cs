using GameServer.Core.World;
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
    // lockstep melee monster in reach the chase, not a swing order. A chase that reaches a monster in a hit's recovery
    // starts when its tick ends the recovery, after the player's. Only a chase the client runs is tested, not a Move
    // that sets LastTarget; an in-place attack, a monster that never walks and a predictive client keep their swing
    // orders.
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
        // A hit's recovery holds the client's monster (0x141e8d900): a chase that comes meanwhile waits, and the monster's
        // own tick starts it in the frame that ends the recovery, after the player's tick. A 1.0 s 0x1d1 written in the
        // frame from 1.023 ends 31 frames later: nothing is ordered in the frame from 2.013, even with the clock past its
        // end before its 0x2b5 has gone out, and the chase in the frame from 2.046 tests the player's cell after his step
        // there, 8.1 cells off (7.3 at the frame's start), so it walks up instead of swinging.
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
            Assert.NotNull(HitReaction.TryFlinch(hurt, 10, 1.024, new Random(1)));
            Game.TakeHitPacket(hurtConn, 0x3000, hurt, hurt.FlinchUntil, 1.024);   // its send gate
        }
        Assert.Equal(1.023 + 31 * 0.033, hurt.FlinchUntil, 9);
        hurtConn.LockstepFrame = (2.013, 2.046);
        Assert.Empty(Game.PlanMonsterAi(hurtConn, new MonsterAiCadence(), now: 2.047).Chases);
        hurtConn.LockstepFrame = (2.046, 2.079);
        var (_, _, recovered, _, _) = Assert.Single(Game.PlanMonsterAi(hurtConn, new MonsterAiCadence(), now: 2.05).Chases);
        Assert.Equal(((93, 102), (101, 101)), Game.PlaceLockstepChase(hurtConn, hurt, recovered, 2.046));
        Assert.Null(hurt.ClientSwingAt);
        Assert.Equal(recovered, hurt.ClientChase);
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

        // A predictive client gets only the leg, and only a lockstep walker keeps the time a stop leaves (PlayerWalk's
        // kept time: LockstepFrameOf and the plan's FrameStart ask conn.LockstepClient). Each leg walks as an order to its
        // cell alone does, from a stand a whole step after the order and under way with the step in progress carried
        // over, also on a Gateway whose lockstep frames run for this connection (RunLockstepFrames, any served one).
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
        Assert.Equal(2.03 + (steps[1].At - steps[0].At), steps[0].At, 9);   // none of the 0.085 s (a lockstep stop keeps 0.105)
        Assert.Equal((193, 140), stopped.Walk.Position(2.03));
        Assert.Equal((legs[0].To, legs[1].To), (walking.Walk.Destination, stopped.Walk.Destination));
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

    // A lockstep client takes no Move whose target is within two cells of the walker (0x1419442d0): no set, no search,
    // its current action goes on (live 2026-11-04: 317 of 317). So such a surround-slot or other plan Move is not sent,
    // a give-up or a freeze sends nothing, a minion that near its approach cell attacks instead, and the estimate keeps
    // the walk it had, a chase's walk-up too; three cells away the Move is placed as before and ends the walk-up.
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
        // The client's sum of squares is a 32-bit int read as unsigned (0x1400f0a10): one past int's top is far, one that
        // wraps to 0 is not (a player's target is its cell plus the 0x18e's i32 offsets, unclamped).
        Assert.True(Game.LockstepTakesMove((0, 0), (46341, 0)));
        Assert.False(Game.LockstepTakesMove((0, 0), (65536, 0)));

        // A give-up gets no order, as GGG's gets none: the estimate walks on to its old target, as the client's monster did
        // after a dropped give-up Move. A frozen walker gets no stop. A predictive client gets both Moves.
        (SpawnedMonster Leaving, SpawnedMonster Frozen) Pair(ClientState on, bool frames)
        {
            PlayerWalk At(int x) => frames ? new(x, 100, 8) { FrameOf = at => (Math.Min(at, 1.0), 1.033) } : new(x, 100, 8);
            var leaving = Drowned(At(230));
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
        // whose searches set up those walks: their search timers run from then (0x141e7bfb0)
        (left.ClientChaseTimerFrom, frozenWalker.ClientChaseTimerFrom) = (0.5, 0.5);
        var plan = Game.PlanMonsterAi(held, new MonsterAiCadence(), now: 1.0);
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
            Assert.Null(Game.PlanMoveAtGate(held, plan, 0x3000, left, Game.NextPlannedAction(left), (226, 100), (228, 100), "flees"));
            Assert.Equal((218, 100), left.Walk.Destination);
            Assert.True(Game.WalkUpOpen(held, left));
            Assert.NotNull(Game.PlanMoveAtGate(held, plan, 0x3000, left, Game.NextPlannedAction(left), (226, 100), (229, 100), "flees"));
            Assert.Equal((229, 100), left.Walk.Destination);
            Assert.False(Game.WalkUpOpen(held, left));
        }
        var predictive = Connection(lockstep: false);
        var (gone, stopped) = Pair(predictive, frames: false);
        var moves = Game.PlanMonsterAi(predictive, new MonsterAiCadence(), now: 1.0).Moves;
        Assert.Equal([(0x3000u, "gives up"), (0x3001u, "is frozen")], moves.Select(mv => (mv.Id, mv.Why)).Order());
        Assert.Equal(moves.Single(mv => mv.Id == 0x3000u).To, gone.Walk.Destination);
        Assert.False(stopped.Walk.Moving(1.1));

        // A fighting minion 2 cells from its approach cell gets its attack aimed at the target instead of that Move: a melee
        // one walks up on the client (0x141e94720) and swings at the walk-up's end, an in-place one shoots from where it
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
        conn.LockstepLatency = LockstepLatency.Unconditional();
        conn.LockstepHold.Press((player.X + 2, player.Y), 0x2909, 0x0408);
        var spent = (conn.LockstepSequence, conn.ActionCount);
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-lockstep-near-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        try { await Game.StartLockstepActions("near", null!, null!, conn, 1.0); }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            TestEnvironment.ReleaseSaves(saves);
        }
        Assert.Single(conn.LockstepTrail);   // the pass ran past the latency gate
        Assert.Equal(spent, (conn.LockstepSequence, conn.ActionCount));
        Assert.Equal((140, 100), conn.Walk.Destination);
        Assert.True(conn.Walk.Moving(1.1));
        Assert.Equal(player, Game.PlaceLockstepPlayerMove(conn, (player.X + 3, player.Y), 1.0));
        Assert.Equal((player.X + 3, player.Y), conn.Walk.Destination);
    }
}

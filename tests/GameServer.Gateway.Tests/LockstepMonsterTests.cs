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
    // lockstep melee monster in reach the chase, not a swing order. Only a chase the client runs is tested, not a Move
    // that sets LastTarget; an in-place attack and a predictive client keep their swing orders.
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
        conn.Monsters[0x3000] = arrived;
        conn.Monsters[0x3001] = walking;
        conn.Monsters[0x3005] = blocked;
        conn.Monsters[0x3006] = standing;
        conn.Monsters[0x3007] = stopped;
        var plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 1.03);
        Assert.Equal([0x3000u, 0x3007u], plan.ArrivedSwings.Select(s => s.Id));
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
        Assert.Empty(plan.Swings);
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
    }

    // On a lockstep client a walker's position reads as of the current frame's start, so the leg's end must be read
    // along the walk itself: read as a position it was the walker's own cell, and every far melee chase went out as a
    // Move onto its own cell (live 2026-11-04: all 3 303 leg orders of two fights; the melee monsters stood). An
    // estimate that finds no way keeps the attack aimed at the player, whose action then ends with no walk-up.
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
    }

    // A lockstep client takes no Move whose target is within two cells of the walker (0x1419442d0): no set, no search,
    // its current action goes on (live 2026-11-04: 317 of 317). So such a surround-slot Move or give-up is not sent, a
    // freeze sends no stop, and the estimate keeps the walk it had; three cells away the Move is placed as before.
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

        // A give-up's Move to the next cell is not sent: the estimate walks on to its old target, as the client's monster
        // did (M0 bench: 69 of 74 still walking 0.5 s later). A frozen walker gets no stop. A predictive client gets both.
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
        var giveUp = Assert.Single(Game.PlanMonsterAi(held, new MonsterAiCadence(), now: 1.0).Moves);
        Assert.Equal((0x3000u, "gives up", (226, 100), (227, 100)), (giveUp.Id, giveUp.Why, giveUp.From, giveUp.To));
        Assert.Equal((140, 100), frozenWalker.Walk.Destination);
        Assert.Equal(((226, 100), false), Game.PlaceLockstepPlanMove(left, giveUp.To, 1.0));
        Assert.Equal((218, 100), left.Walk.Destination);
        Assert.True(left.Walk.Moving(1.5));
        var predictive = Connection(lockstep: false);
        var (gone, stopped) = Pair(predictive, frames: false);
        var moves = Game.PlanMonsterAi(predictive, new MonsterAiCadence(), now: 1.0).Moves;
        Assert.Equal([(0x3000u, "gives up"), (0x3001u, "is frozen")], moves.Select(mv => (mv.Id, mv.Why)).Order());
        Assert.Equal(moves.Single(mv => mv.Id == 0x3000u).To, gone.Walk.Destination);
        Assert.False(stopped.Walk.Moving(1.1));

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

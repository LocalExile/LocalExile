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

    static ClientState Connection(bool lockstep) => new(new(1, "chase", 300, 300, 100, 100, null, []), 1, 1)
    {
        Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500, PlayerSent = true, LockstepClient = lockstep,
    };

    static SpawnedMonster Drowned(PlayerWalk walk) => new("Drowned", walk, aggroRange: 60, stopRange: 2, maxLife: 23)
    {
        Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Aggro = true, LastTarget = (100, 100),
    };

    // A melee chase order's walk-up swings by itself on a lockstep client once the player is in its reach, so the server
    // takes that swing instead of ordering a second one; a walk-up still out of reach, and a predictive client, get the
    // chase or swing order as before.
    [Fact]
    public void A_chase_walk_up_that_got_there_swings_without_a_second_order_on_a_lockstep_client()
    {
        var conn = Connection(lockstep: true);
        var arrived = Drowned(new PlayerWalk(104, 100, 8));   // at rest since 0, four cells from the player
        var walking = Drowned(new PlayerWalk(140, 100, 8));
        walking.Walk.Order(100, 100, now: 1);   // five seconds from the player's cell
        conn.Monsters[0x3000] = arrived;
        conn.Monsters[0x3001] = walking;
        var plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 1);
        Assert.Equal([0x3000u], plan.ArrivedSwings.Select(s => s.Id));
        // In reach while standing: the swing began within the last AI step.
        Assert.Equal(1 - MonsterAiCadence.StepSeconds, plan.ArrivedSwings[0].BegunAt, 6);
        Assert.Equal(1 - MonsterAiCadence.StepSeconds + 2.0 + conn.LockstepActionTail, arrived.BusyUntil, 3);
        Assert.Null(arrived.LastTarget);
        Assert.Equal(arrived.ActionGeneration, arrived.SwingGeneration);
        Assert.DoesNotContain(0x3000u, plan.Swings.Select(s => s.Id));
        Assert.Equal([0x3001u], plan.Legs.Select(c => c.Id));   // 40 cells off: its chase goes in legs

        // In reach on the way (its walk passes the player): the swing begins at this tick and the walk ends here.
        var met = Drowned(new PlayerWalk(140, 105, 8));
        met.Walk.Order(96, 105, now: 0);   // past the player five cells off: within reach from 4.3 s, arriving at 5.5 s
        conn.Monsters[0x3002] = met;
        plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 4.5);
        Assert.Contains(0x3002u, plan.ArrivedSwings.Select(s => s.Id));
        Assert.Equal(4.5, plan.ArrivedSwings.Single(s => s.Id == 0x3002u).BegunAt, 6);
        Assert.False(met.Walk.Moving(4.6));
        // At the path's end with the player gone on: no swing there (the client's monster goes on after the player), the
        // chase is ordered again.
        var late = Drowned(new PlayerWalk(120, 100, 8));
        late.Walk.Order(118, 100, now: 0);   // a two-cell walk, over at 0.25 s, ending 18 cells from the player
        conn.Monsters[0x3003] = late;
        plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 6);
        Assert.DoesNotContain(0x3003u, plan.ArrivedSwings.Select(s => s.Id));
        Assert.Contains(0x3003u, plan.Chases.Select(c => c.Id));
        // A walk that a body stopped short is no arrival: the chase goes on (ordered again) instead of a swing there.
        var blocked = Drowned(new PlayerWalk(130, 100, 8));
        blocked.Walk.Order(100, 100, now: 0);
        blocked.Walk.StopAt(0.5);   // a step found blocked half a second in, 26 cells from the player
        conn.Monsters[0x3005] = blocked;
        plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 7);
        Assert.DoesNotContain(0x3005u, plan.ArrivedSwings.Select(s => s.Id));
        Assert.Contains(0x3005u, plan.Legs.Select(c => c.Id));   // 30 cells off: ordered again, in a leg
        // A ranged monster's in-place attack leaves its Move running to the end, on a lockstep client as on a predictive
        // one: the estimate keeps its walk.
        SpawnedMonster Spitter(PlayerWalk walk) => new("Spitter", walk, aggroRange: 60, stopRange: 2, maxLife: 10)
        {
            Attack = new(0x1535, 1.0, 30, DamagePacket.Empty) { Ranged = true }, Aggro = true, LastTarget = (120, 100),
        };
        var shooter = Spitter(new PlayerWalk(130, 100, 8));
        shooter.Walk.Order(120, 100, now: 6);
        conn.Monsters[0x3004] = shooter;
        plan = Game.PlanMonsterAi(conn, new MonsterAiCadence(), now: 6.5);
        Assert.Contains(0x3004u, plan.Swings.Select(s => s.Id));
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
    }
}

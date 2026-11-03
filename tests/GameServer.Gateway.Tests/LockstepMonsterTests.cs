using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

public class LockstepMonsterTests
{
    static ClientState Connection(bool lockstep) => new(new(1, "chase", 300, 300, 100, 100, null, []), 1, 1)
    {
        Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500, PlayerSent = true, LockstepClient = lockstep,
    };

    static SpawnedMonster Drowned(PlayerWalk walk) => new("Drowned", walk, aggroRange: 60, stopRange: 2, maxLife: 23)
    {
        Attack = new(0x3188, 2.0, 8, DamagePacket.Empty), Aggro = true, LastTarget = (100, 100),
    };

    // A melee chase order's walk-up swings by itself on a lockstep client when it gets there, so the server takes that
    // swing from the walk-up's end instead of ordering a second one; a walk-up still under way, and a predictive client,
    // get the swing order as before.
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
        Assert.Equal(0, plan.ArrivedSwings[0].BegunAt);
        Assert.Equal(2.0 + conn.LockstepActionTail, arrived.BusyUntil, 3);
        Assert.Null(arrived.LastTarget);
        Assert.Equal(arrived.ActionGeneration, arrived.SwingGeneration);
        Assert.DoesNotContain(0x3000u, plan.Swings.Select(s => s.Id));
        Assert.Equal([0x3001u], plan.Chases.Select(c => c.Id));

        var predictive = Connection(lockstep: false);
        var monster = Drowned(new PlayerWalk(104, 100, 8));
        predictive.Monsters[0x3000] = monster;
        plan = Game.PlanMonsterAi(predictive, new MonsterAiCadence(), now: 1);
        Assert.Empty(plan.ArrivedSwings);
        Assert.Equal([0x3000u], plan.Swings.Select(s => s.Id));
    }
}

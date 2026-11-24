using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

static class IncomingDamagingAilmentTestCases
{
    sealed class UpperRoll : Random
    {
        public override double NextDouble() => 0.999999;
        public override int Next(int minValue, int maxValue) => maxValue - 1;
        public override int Next(int maxValue) => maxValue - 1;
    }

    static Player Fresh() => new(new(1, "incoming-damaging-ailments", 300, 300, 100, 100, null, []), 1, 1)
    {
        Pools = new(1, 10000, 100, 0, 0), PlayerLife = 10000,
        IncomingHitRandom = new UpperRoll(),
    };

    static Dictionary<uint, int> Stats(params (string Name, int Value)[] pairs) =>
        pairs.ToDictionary(p => Stat.Of(p.Name), p => p.Value);

    public static void Monster_applications_use_final_avoidance_and_rolled_damage_ranges()
    {
        var monster = new SpawnedMonster("ailment-source", new(0, 0, 0), 0, 0, 1000)
        {
            Mods = new([], [], new MonsterModEffects(new Dictionary<uint, int>
            {
                [Stat.GlobalBleedOnHit] = 1, [Stat.GlobalPoisonOnHit] = 1,
            })),
        };
        var range = DamagePacket.Of(DamageType.Physical, 10, 100);
        var player = Fresh();
        player.Clock.Stop();
        double now = player.Clock.Elapsed.TotalSeconds;
        Game.ApplyMonsterDamagingAilmentsLocked(player, 1, monster, range, false, now, prepared: Stats());
        Assert.Equal(70, player.IncomingAilments.BleedPerSecond(now), 8);
        Assert.Equal(30, player.IncomingAilments.PoisonPerSecond(now), 8);

        foreach (var prevented in new[]
        {
            Stats(("avoid_bleed_%", 100), ("avoid_poison_%", 100)),
            Stats(("virtual_immune_to_bleeding", 1), ("cannot_be_poisoned", 1)),
        })
        {
            player.IncomingAilments.Clear();
            Game.ApplyMonsterDamagingAilmentsLocked(player, 1, monster, range, false, now, prepared: prevented);
            Assert.True(player.IncomingAilments.Idle);
        }
        player.IncomingAilments.Clear();
        Game.ApplyMonsterDamagingAilmentsLocked(player, 1, monster, range, true, now, prepared: Stats());
        Assert.True(player.IncomingAilments.Idle);
        Game.ApplyMonsterDamagingAilmentsLocked(player, 1, monster, range, false, now, attack: false, prepared: Stats());
        Assert.Equal(70, player.IncomingAilments.BleedPerSecond(now), 8);
        player.InvalidateIncomingHits();
        Assert.True(player.IncomingAilments.Idle);
        player.Closed = true;
    }

    public static void Resource_writer_preserves_stack_intervals_routing_and_paid_cleanse_protection()
    {
        var player = Fresh();
        player.Clock.Stop();
        double now = player.Clock.Elapsed.TotalSeconds;
        player.Auras[1] = new(1, 0, "Vitality", "Vitality", 1, 153, 500, 0, ReservesLife: true);
        int cap = player.PlayerLifeCap;
        Assert.Equal(500u, player.PlayerReservedLife);
        Assert.Equal(10000u, player.PlayerLife);
        Assert.Equal(cap, player.PlayerLifeCap);
        player.Auras.Clear();
        var mixed = new IncomingDamagingAilments();
        mixed.ApplyBleed(0, 100, 2);
        double nonChaosCarry = 0, chaosCarry = 0;
        int Ralakesh(uint stat) => stat == Stat.NoExtraBleedDamageWhileMoving ? 1
            : stat == Stat.PhysicalDamageOverTimeTakenPlusPctWhileMoving ? -25 : 0;
        Assert.Equal(175, mixed.Advance(0, 2, player.Pools, player.EnergyShield, Ralakesh, 0, 75, 0, 75,
            false, false, false, (_, _) => 1, ref nonChaosCarry, ref chaosCarry));
        player.IncomingAilments.ApplyBleed(now, 10, 2);
        player.IncomingAilments.ApplyBleed(now, 5, 4);
        player.IncomingAilments.ApplyPoison(now + 1, 10, 2);
        player.EnergyShield.SetMaximum(100, refill: true);
        Game.AdvancePlayerAilmentsLocked(player, now, now + 2, Stats());
        Assert.Equal(80u, player.EnergyShield.Current);
        Assert.Equal(9990u, player.PlayerLife);
        Game.AdvancePlayerAilmentsLocked(player, now, now + 2, Stats());
        Assert.Equal(80u, player.EnergyShield.Current);
        Assert.Equal(9990u, player.PlayerLife);
        Game.AdvancePlayerAilmentsLocked(player, now + 2, now + 4, Stats());
        Assert.Equal(70u, player.EnergyShield.Current);
        Assert.Equal(9980u, player.PlayerLife);
        Assert.True(player.IncomingAilments.Idle);

        player.IncomingAilments.Clear();
        player.IncomingAilments.ApplyBleed(now, 10, 5);
        Assert.True(player.IncomingAilments.RemoveOrProtectBleeding(now, 2));
        Assert.False(player.IncomingAilments.ApplyBleed(now + 1, 10, 5));
        Assert.True(player.IncomingAilments.ApplyBleed(now + 2, 10, 5));
        Assert.False(player.IncomingAilments.RemoveOrProtectPoison(now, 2));
        Assert.True(player.IncomingAilments.ApplyPoison(now, 10, 5));
        player.IncomingAilments.Reconcile(name => name == Stat.CannotBePoisoned ? 1 : 0);
        Assert.Equal(0, player.IncomingAilments.PoisonStacks(now));

        player.IncomingAilments.Clear();
        player.EnergyShield.Empty();
        player.IncomingAilments.ApplyBleed(now, 10, 5);
        player.EnergyShield.Empty();
        player.Walk.Order(player.Walk.Position(now).X + 100, player.Walk.Position(now).Y, now);
        Assert.True(player.Walk.Moving(now));
        uint before = player.PlayerLife;
        Game.AdvancePlayerAilmentsLocked(player, now, now + 1, Stats());
        Assert.Equal(before - 30, player.PlayerLife);
        player.IncomingAilments.Clear();
        player.IncomingAilments.ApplyPoison(now, .5, 4);
        before = player.PlayerLife;
        for (int i = 0; i < 4; i++) Game.AdvancePlayerAilmentsLocked(player, now + i, now + i + 1, Stats());
        Assert.Equal(before - 2, player.PlayerLife);

        player.IncomingAilments.Clear();
        player.IncomingAilments.ApplyPoison(now, 100000, 2);
        Game.AdvancePlayerAilmentsLocked(player, now, now + 1, Stats());
        Assert.Equal(0u, player.PlayerLife);
        Assert.True(player.IncomingAilments.Idle);
        Assert.Equal(0u, player.PlayerReservedLife);
        Assert.Equal(0u, player.PlayerLife);
        player.Closed = true;
    }
}

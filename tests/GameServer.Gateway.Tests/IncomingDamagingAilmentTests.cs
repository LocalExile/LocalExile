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

    static ClientState Fresh() => new(new(1, "incoming-damaging-ailments", 300, 300, 100, 100, null, []), 1, 1)
    {
        Pools = new(1, 10000, 100, 0, 0), PlayerLife = 10000,
        IncomingHitRandom = new UpperRoll(),
    };

    static Dictionary<uint, int> Stats(params (string Name, int Value)[] pairs) =>
        pairs.ToDictionary(p => Program.Stats.Key(p.Name)!.Value, p => p.Value);

    public static void Monster_applications_use_final_avoidance_and_rolled_damage_ranges()
    {
        var monster = new SpawnedMonster("ailment-source", new(0, 0, 0), 0, 0, 1000)
        {
            Mods = new([], [], new MonsterModEffects(new Dictionary<string, int>
            {
                ["global_bleed_on_hit"] = 1, ["global_poison_on_hit"] = 1,
            })),
        };
        var range = DamagePacket.Of(DamageType.Physical, 10, 100);
        var conn = Fresh();
        conn.Clock.Stop();
        double now = conn.Clock.Elapsed.TotalSeconds;
        Game.ApplyMonsterDamagingAilmentsLocked(conn, 1, monster, range, false, now, prepared: Stats());
        Assert.Equal(70, conn.IncomingAilments.BleedPerSecond(now), 8);
        Assert.Equal(30, conn.IncomingAilments.PoisonPerSecond(now), 8);

        foreach (var prevented in new[]
        {
            Stats(("avoid_bleed_%", 100), ("avoid_poison_%", 100)),
            Stats(("virtual_immune_to_bleeding", 1), ("cannot_be_poisoned", 1)),
        })
        {
            conn.IncomingAilments.Clear();
            Game.ApplyMonsterDamagingAilmentsLocked(conn, 1, monster, range, false, now, prepared: prevented);
            Assert.True(conn.IncomingAilments.Idle);
        }
        conn.IncomingAilments.Clear();
        Game.ApplyMonsterDamagingAilmentsLocked(conn, 1, monster, range, true, now, prepared: Stats());
        Assert.True(conn.IncomingAilments.Idle);
        Game.ApplyMonsterDamagingAilmentsLocked(conn, 1, monster, range, false, now, attack: false, prepared: Stats());
        Assert.Equal(70, conn.IncomingAilments.BleedPerSecond(now), 8);
        conn.InvalidateIncomingHits();
        Assert.True(conn.IncomingAilments.Idle);
        conn.Closed = true;
    }

    public static void Resource_writer_preserves_stack_intervals_routing_and_paid_cleanse_protection()
    {
        var conn = Fresh();
        conn.Clock.Stop();
        double now = conn.Clock.Elapsed.TotalSeconds;
        conn.Auras[1] = new(1, 0, "Vitality", "Vitality", 1, 153, 500, 0, ReservesLife: true);
        int cap = conn.PlayerLifeCap;
        Assert.Equal(GameServer.Core.Protocol.Instance.InstanceProtocol.LifeUpdate(Program.PlayerId, 10000, 500),
            Game.PlayerLifeUpdate(conn));
        Assert.Equal(10000u, conn.PlayerLife);
        Assert.Equal(cap, conn.PlayerLifeCap);
        conn.Auras.Clear();
        conn.IncomingAilments.ApplyBleed(now, 10, 2);
        conn.IncomingAilments.ApplyBleed(now, 5, 4);
        conn.IncomingAilments.ApplyPoison(now + 1, 10, 2);
        conn.EnergyShield.SetMaximum(100, refill: true);
        Game.AdvancePlayerAilmentsLocked(conn, now, now + 2, Stats());
        Assert.Equal(80u, conn.EnergyShield.Current);
        Assert.Equal(9990u, conn.PlayerLife);
        Game.AdvancePlayerAilmentsLocked(conn, now, now + 2, Stats());
        Assert.Equal(80u, conn.EnergyShield.Current);
        Assert.Equal(9990u, conn.PlayerLife);
        Game.AdvancePlayerAilmentsLocked(conn, now + 2, now + 4, Stats());
        Assert.Equal(70u, conn.EnergyShield.Current);
        Assert.Equal(9980u, conn.PlayerLife);
        Assert.True(conn.IncomingAilments.Idle);

        conn.IncomingAilments.Clear();
        conn.IncomingAilments.ApplyBleed(now, 10, 5);
        Assert.True(conn.IncomingAilments.RemoveOrProtectBleeding(now, 2));
        Assert.False(conn.IncomingAilments.ApplyBleed(now + 1, 10, 5));
        Assert.True(conn.IncomingAilments.ApplyBleed(now + 2, 10, 5));
        Assert.False(conn.IncomingAilments.RemoveOrProtectPoison(now, 2));
        Assert.True(conn.IncomingAilments.ApplyPoison(now, 10, 5));
        conn.IncomingAilments.Reconcile(name => name == "cannot_be_poisoned" ? 1 : 0);
        Assert.Equal(0, conn.IncomingAilments.PoisonStacks(now));

        conn.IncomingAilments.Clear();
        conn.EnergyShield.Empty();
        conn.IncomingAilments.ApplyBleed(now, 10, 5);
        conn.EnergyShield.Empty();
        conn.Walk.Order(conn.Walk.Position(now).X + 100, conn.Walk.Position(now).Y, now);
        Assert.True(conn.Walk.Moving(now));
        uint before = conn.PlayerLife;
        Game.AdvancePlayerAilmentsLocked(conn, now, now + 1, Stats());
        Assert.Equal(before - 30, conn.PlayerLife);
        conn.IncomingAilments.Clear();
        conn.IncomingAilments.ApplyPoison(now, .5, 4);
        before = conn.PlayerLife;
        for (int i = 0; i < 4; i++) Game.AdvancePlayerAilmentsLocked(conn, now + i, now + i + 1, Stats());
        Assert.Equal(before - 2, conn.PlayerLife);

        conn.IncomingAilments.Clear();
        conn.IncomingAilments.ApplyPoison(now, 100000, 2);
        Game.AdvancePlayerAilmentsLocked(conn, now, now + 1, Stats());
        Assert.Equal(0u, conn.PlayerLife);
        Assert.True(conn.IncomingAilments.Idle);
        Assert.Equal(GameServer.Core.Protocol.Instance.InstanceProtocol.LifeUpdate(Program.PlayerId, 0, 0),
            Game.PlayerLifeUpdate(conn));
        Assert.Equal(0u, conn.PlayerLife);
        conn.Closed = true;
    }
}

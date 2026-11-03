using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MonsterPoisonsTests
{
    [Fact]
    public void Poisons_stack_and_deal_concurrent_damage()
    {
        var poisons = new MonsterPoisons();
        Assert.True(poisons.Idle);
        Assert.False(poisons.Poisoned);

        // Add two poison stacks of 100 DPS lasting 2 seconds at t=0
        Assert.True(poisons.Add(0, 100, 2));
        Assert.True(poisons.Add(0, 100, 2));

        Assert.True(poisons.Poisoned);
        Assert.Equal(2, poisons.Stacks(0.5));
        Assert.Equal(200, poisons.TotalDps(0.5));

        // Advance 1 second with 100% factor (0% resistance):
        // 200 DPS * 1s = 200 damage
        int damage = poisons.Advance(1.0, 1.0);
        Assert.Equal(200, damage);
        Assert.Equal(2, poisons.Stacks(1.0));

        // Add a third stack at t=1.0 lasting 2s (expires at t=3.0)
        Assert.True(poisons.Add(1.0, 150, 2));
        Assert.Equal(3, poisons.Stacks(1.5));
        Assert.Equal(350, poisons.TotalDps(1.5));

        // Advance to t=2.0 (stacks 1 and 2 expire at 2.0):
        // 1s elapsed at 350 DPS = 350 damage
        damage = poisons.Advance(2.0, 1.0);
        Assert.Equal(350, damage);

        // Advance to t=3.0: only stack 3 was active from 2.0 to 3.0 (150 DPS * 1s = 150 damage)
        damage = poisons.Advance(3.0, 1.0);
        Assert.Equal(150, damage);

        // Stacks have all expired at t=3.0
        Assert.Equal(0, poisons.Stacks(3.5));
        Assert.False(poisons.Poisoned);
    }

    [Fact]
    public void Chaos_resistance_mitigates_poison_damage()
    {
        var poisons = new MonsterPoisons();
        poisons.Add(0, 100, 2);

        // Factor 0.5 (e.g. 50% chaos resistance):
        // 100 DPS * 1s * 0.5 = 50 damage
        int damage = poisons.Advance(1.0, 0.5);
        Assert.Equal(50, damage);
    }

    // Poison is gated on its own type: a chaos prohibition stops it on a physical-only hit, another type's does not.
    // Its damage and duration are the hit's poison rows, not the landed hit, which already carries the hit's increases.
    [Fact]
    public void A_chaos_prohibition_stops_poison_from_a_physical_hit()
    {
        var conn = new ClientState(new(1, "poison", 300, 300, 100, 100, null, []), 1, 1);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var rows = new MonsterPoisons.Settings(100, Min: 10, Max: 10, CriticalMin: 20, CriticalMax: 20, Seconds: 2);
        SpawnedMonster Poisoned(bool critical, params (string Name, int Value)[] stats)
        {
            var m = new SpawnedMonster("poisoned", new(105, 100, 0), 0, 0, 1000);
            var player = stats.Append((Name: "chance_to_poison_on_hit_%", Value: 100))
                .ToDictionary(s => Program.Stats.Key(s.Name) ?? throw new InvalidOperationException(s.Name), s => s.Value);
            Game.PoisonOnHitLocked(conn, m, physical, spell: false, critical, player, null, rows, now: 10, new Random(1));
            return m;
        }
        bool Poisons(params (string Name, int Value)[] stats) => Poisoned(false, stats).Poisons.Poisoned;

        Assert.Equal(10, Poisoned(false).Poisons.TotalDps(10.5));
        Assert.Equal(20, Poisoned(true).Poisons.TotalDps(10.5));
        Assert.Equal(12, Poisoned(false).Poisons.Until, 6);
        Assert.True(Poisons());
        Assert.False(Poisons(("base_deal_no_chaos_damage", 1)));
        Assert.False(Poisons(("deal_no_non_physical_damage", 1)));
        Assert.True(Poisons(("base_deal_no_cold_damage", 1)));
    }
}

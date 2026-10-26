using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GamblerModifiersTests
{
    [Fact]
    public void Growing_Accumulator_rolls_triple_damage_for_the_whole_hit_and_keeps_its_less_damage_penalty()
    {
        var mods = new GamblerModifiers(TripleDamageChance: 40, DamageMorePct: -30);
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100));
        var triple = mods.RollOutgoingHit(hit, new SequenceRandom(39, 210, 210));
        var ordinary = mods.RollOutgoingHit(hit, new SequenceRandom(40, 70, 70));

        Assert.Equal(420, triple.MinTotal);
        Assert.Equal(140, ordinary.MinTotal);
        // The less modifier also applies to DoT. Triple damage and damage luck never roll on a DoT tick.
        Assert.Equal(0.7, mods.DamageFactor, 8);
    }

    [Fact]
    public void Spectacular_Parlay_has_lucky_crits_but_unlucky_hit_damage()
    {
        var mods = new GamblerModifiers(DamageExtraRolls: -1, CriticalExtraRolls: 1);
        Assert.True(mods.IsCritical(500, new SequenceRandom(600, 400)));
        var rolled = mods.RollOutgoingHit(DamagePacket.Of(DamageType.Fire, 1, 100), new SequenceRandom(80, 20));
        Assert.Equal(20, rolled.MinTotal);
    }

    [Fact]
    public void Extra_critical_rolls_change_only_their_own_chance_and_cost_no_roll_off_low_life()
    {
        // Chance 5% (permyriad 500): a first roll of 600 fails, a second of 400 succeeds.
        // Final Blow grants the extra crit roll through the real stat catalog, resolved with everything else.
        var from = GamblerModifiers.From(name => name == "extra_critical_rolls_while_on_low_life" ? 1 : 0);
        Assert.Equal(1, from.LowLifeCriticalExtraRolls);
        // Without the condition the extra roll is not in the count: one Next call, 600 does not crit.
        var random = new SequenceRandom(600);
        Assert.False(new GamblerModifiers(LowLifeCriticalExtraRolls: 1).IsCritical(500, random));
        Assert.Equal(1, random.Calls);
        // On low life the second roll can save it (PoB's 1-(1-p)^2 lucky form, not a forced success or a more).
        Assert.True(new GamblerModifiers(LowLifeCriticalExtraRolls: 1).IsCritical(500, new SequenceRandom(600, 400), onLowLife: true));
        // A 100% or 0% chance resolves before any roll is spent (RandomRolls guards the bounds).
        Assert.True(new GamblerModifiers(LowLifeCriticalExtraRolls: 1).IsCritical(10_000, new SequenceRandom(), onLowLife: true));
        Assert.False(new GamblerModifiers(LowLifeCriticalExtraRolls: 1).IsCritical(0, new SequenceRandom(600), onLowLife: true));
        // The low-life lucky roll and a general unlucky roll cancel; the sum drives the count in both states.
        var cancel = new GamblerModifiers(CriticalExtraRolls: -1, LowLifeCriticalExtraRolls: 1);
        Assert.False(cancel.IsCritical(500, new SequenceRandom(600), onLowLife: true)); // -1+1=0: one roll, failure
        // Off low life only the -1 unlucky general roll remains: both values (one true roll, one) must succeed.
        Assert.False(cancel.IsCritical(500, new SequenceRandom(700, 600)));
        Assert.True(cancel.IsCritical(500, new SequenceRandom(400, 400)));
        // The ordinary lucky extra roll keeps costing and compounding exactly as before.
        var lucky = new GamblerModifiers(CriticalExtraRolls: 1);
        Assert.True(lucky.IsCritical(500, new SequenceRandom(600, 400)));
        var counter = new SequenceRandom(600, 400);
        lucky.IsCritical(500, counter);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public void Overly_Confident_uses_the_lower_roll_of_each_enemy_damage_type()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 1, 100).Add(DamageType.Fire, new(1, 100));
        var rolled = new GamblerModifiers(EnemyDamageExtraRolls: -1).RollIncomingHit(hit, new SequenceRandom(80, 20, 10, 90));
        Assert.Equal(20, rolled[DamageType.Physical].Min);
        Assert.Equal(10, rolled[DamageType.Fire].Min);
    }

    [Fact]
    public void Reversed_Odds_uses_one_event_for_all_elements_but_never_inverts_chaos_or_physical()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Fire, new(100, 100)).Add(DamageType.Cold, new(100, 100)).Add(DamageType.Chaos, new(100, 100));
        var random = new SequenceRandom(49);
        var mitigated = new GamblerModifiers(InvertElementalResistanceChance: 50).MitigateOutgoingHit(hit,
            type => new(type == DamageType.Fire ? 50 : type == DamageType.Cold ? 25 : 40), random);

        Assert.Equal(100, mitigated[DamageType.Physical].Min);
        Assert.Equal(150, mitigated[DamageType.Fire].Min);
        Assert.Equal(125, mitigated[DamageType.Cold].Min);
        Assert.Equal(60, mitigated[DamageType.Chaos].Min);
        Assert.Equal(1, random.Calls);
    }

    [Fact]
    public void Inversion_caps_real_resistance_first_and_can_hurt_against_negative_resistance()
    {
        var mods = new GamblerModifiers(InvertElementalResistanceChance: 100);
        var hit = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(175, mods.MitigateOutgoingHit(hit, _ => new(200, 75), new SequenceRandom()).MinTotal);
        Assert.Equal(50, mods.MitigateOutgoingHit(hit, _ => new(-50), new SequenceRandom()).MinTotal);
        // Do not recap inverted -200 to +90: that would give the inversion a benefit it does not grant.
        Assert.Equal(0, mods.MitigateOutgoingHit(hit, _ => new(-200), new SequenceRandom()).MinTotal);
    }

    [Fact]
    public void Risky_Exploit_overrides_the_maximum_for_one_hit_without_changing_other_damage_types()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Fire, new(100, 100)).Add(DamageType.Chaos, new(100, 100));
        var mods = new GamblerModifiers(EnemyHitsNinetyResistanceChance: 50);
        var random = new SequenceRandom(49);
        var mitigated = mods.MitigateIncomingHit(hit, _ => new(25, 75), random);
        Assert.Equal(100, mitigated[DamageType.Physical].Min, 8); // armour is handled by the caller
        Assert.Equal(10, mitigated[DamageType.Fire].Min, 8);
        Assert.Equal(75, mitigated[DamageType.Chaos].Min, 8);
        Assert.Equal(1, random.Calls);
        Assert.Equal(75, mods.MitigateIncomingHit(hit, _ => new(25, 75), new SequenceRandom(50))[DamageType.Fire].Min, 8);
    }

    [Fact]
    public void Actual_Gambler_nodes_resolve_to_scoped_roll_policies()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var allocated = new PassiveTree(data).Stats([15874, 5523, 16638, 25530, 62445]);
        var names = new StatCatalog(data);
        var mods = GamblerModifiers.From(name => names.Key(name) is uint key ? allocated.GetValueOrDefault(key) : 0);
        Assert.Equal((-1, 1, -1, 40, -30, 50, 50, 0), (mods.DamageExtraRolls, mods.CriticalExtraRolls, mods.EnemyDamageExtraRolls,
            mods.TripleDamageChance, mods.DamageMorePct, mods.InvertElementalResistanceChance, mods.EnemyHitsNinetyResistanceChance,
            mods.LowLifeCriticalExtraRolls));
    }

    private static string? Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "tables", "PassiveSkills.json"))
                && File.Exists(Path.Combine(dir.FullName, "data", "tables", "Stats.json"))) return Path.Combine(dir.FullName, "data");
        return null;
    }

    private sealed class SequenceRandom(params int[] values) : Random
    {
        private readonly Queue<int> _values = new(values);
        public int Calls { get; private set; }
        public override int Next(int minValue, int maxValue)
        {
            Calls++;
            int result = _values.Dequeue();
            Assert.InRange(result, minValue, maxValue - 1);
            return result;
        }
    }
}

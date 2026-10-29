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
        var mods = new GamblerModifiers(DamageExtraRolls: -1);
        var rolled = mods.RollOutgoingHit(DamagePacket.Of(DamageType.Fire, 1, 100), new SequenceRandom(80, 20));
        Assert.Equal(20, rolled.MinTotal);
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
        Assert.Equal((-1, -1, 40, -30, 50, 50), (mods.DamageExtraRolls, mods.EnemyDamageExtraRolls,
            mods.TripleDamageChance, mods.DamageMorePct, mods.InvertElementalResistanceChance, mods.EnemyHitsNinetyResistanceChance));
    }

    private static string? Data()
    {
        return File.Exists("data/tables/PassiveSkills.json") && File.Exists("data/tables/Stats.json") ? "data" : null;
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

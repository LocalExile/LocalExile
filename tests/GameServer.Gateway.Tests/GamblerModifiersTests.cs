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

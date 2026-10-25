using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class RandomRollsTests
{
    [Theory]
    [InlineData(0, 80)]
    [InlineData(1, 80)]
    [InlineData(-1, 20)]
    public void Damage_luck_keeps_the_highest_or_lowest_roll(int extra, int expected)
    {
        var random = new SequenceRandom(80, 20);
        Assert.Equal(expected, new DamageRange(1, 100).Roll(random, extra));
        Assert.Equal(extra == 0 ? 1 : 2, random.Calls);
    }

    [Fact]
    public void Additional_rolls_are_counted_and_damage_types_are_rolled_independently()
    {
        var random = new SequenceRandom(80, 40, 60, 20, 90, 10);
        var packet = DamagePacket.Of(DamageType.Physical, 1, 100).Add(DamageType.Fire, new(1, 100));
        var rolled = packet.Roll(random, extraRolls: 2);
        Assert.Equal(80, rolled[DamageType.Physical].Min);
        Assert.Equal(90, rolled[DamageType.Fire].Min);
        Assert.Equal(6, random.Calls);
    }

    [Theory]
    [InlineData(1, 80, 20, true)]
    [InlineData(-1, 20, 80, false)]
    [InlineData(0, 80, 20, false)]
    public void Lucky_chance_succeeds_on_either_roll_and_unlucky_requires_both(int extra, int first, int second, bool expected) =>
        Assert.Equal(expected, RandomRolls.Chance(50, 100, new SequenceRandom(first, second), extra));

    [Theory]
    [InlineData(false, false, 40)]
    [InlineData(true, false, 64)]
    [InlineData(false, true, 16)]
    [InlineData(true, true, 40)]
    public void Lucky_and_unlucky_block_cancel_and_use_the_correct_probability(bool lucky, bool unlucky, int expected)
    {
        Assert.Equal(expected, DamageModel.EffectiveBlockChance(40, lucky, unlucky), 8);
        Assert.True(DamageModel.Blocks(40, expected - 0.1, lucky, unlucky));
        Assert.False(DamageModel.Blocks(40, expected + 0.1, lucky, unlucky));
    }

    [Fact]
    public void Impossible_and_guaranteed_chances_do_not_need_a_random_draw()
    {
        var random = new SequenceRandom();
        Assert.False(RandomRolls.Chance(-1, 100, random));
        Assert.True(RandomRolls.Chance(101, 100, random));
        Assert.Equal(0, random.Calls);
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

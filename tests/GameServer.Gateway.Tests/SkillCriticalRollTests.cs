namespace GameServer.Gateway.Tests;

public class SkillCriticalRollTests
{
    sealed class Draws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(10000, maxValue);
            Assert.True(Calls < values.Length, "Unexpected critical draw");
            return values[Calls++];
        }
    }

    [Theory]
    [InlineData(0, 4000, 6000, false, 1)]
    [InlineData(1, 9000, 2000, true, 2)]
    [InlineData(-1, 1000, 4000, false, 2)]
    public void One_roll_is_compared_to_each_hits_chance_not_cached_as_a_boolean(
        int luck, int first, int second, bool lowChanceResult, int draws)
    {
        var random = new Draws(first, second);
        var roll = new SkillCriticalRoll(luck);
        Assert.Equal(lowChanceResult, roll.Check(3000, random));
        Assert.True(roll.Check(8000, random));
        Assert.Equal(lowChanceResult, roll.Check(3000, random));
        Assert.Equal(draws, random.Calls);
    }

    [Fact]
    public void Prohibition_and_zero_chance_do_not_consume_or_discard_the_roll()
    {
        var random = new Draws(2000, 4000);
        var roll = new SkillCriticalRoll(1);
        Assert.False(roll.Check(0, random));
        Assert.False(roll.Check(-1, random));
        Assert.Equal(0, random.Calls);
        Assert.True(roll.Check(3000, random));
        Assert.False(roll.Check(0, random));
        Assert.True(roll.Check(3000, random));
        Assert.Equal(2, random.Calls);
    }

    [Fact]
    public void Separate_skill_uses_get_separate_rolls()
    {
        var random = new Draws(1000, 9000);
        Assert.True(new SkillCriticalRoll(0).Check(5000, random));
        Assert.False(new SkillCriticalRoll(0).Check(5000, random));
        Assert.Equal(2, random.Calls);
    }
}

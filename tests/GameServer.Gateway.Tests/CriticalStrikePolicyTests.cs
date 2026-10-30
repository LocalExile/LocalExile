namespace GameServer.Gateway.Tests;

public class CriticalStrikePolicyTests
{
    sealed class CountRandom(params int[] values) : Random
    {
        public int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Assert.Equal(10_000, maxValue);
            Assert.True(Calls < values.Length, "Unexpected extra critical draw");
            return values[Calls++];
        }
    }

    [Theory]
    [InlineData(50u, 100u, true)]
    public void Living_low_life_uses_the_inclusive_maximum_life_boundary(uint life, uint maximum, bool expected)
        => Assert.Equal(expected, CriticalStrikePolicy.OnLowLife(life, maximum));

}

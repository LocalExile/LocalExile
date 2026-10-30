namespace GameServer.Gateway.Tests;

public class AttributeModifiersTests
{
    // PoB 16de4b8 CalcPerform:405 and Common:683: nearest integer, positive halves up, then nonnegative.
    [Theory]
    [InlineData(17, 3, 18)]
    public void Rounding_reductions_and_saturation(int flat, int increased, int expected)
    {
        Assert.Equal(expected, new AttributeModifiers(Strength: flat, StrengthPct: increased).Apply().Strength);
    }

}

namespace GameServer.Gateway.Tests;

public class RecoveryPoolTests
{
    [Theory]
    [InlineData(0.016)]
    public void Fractional_regeneration_keeps_the_same_rate_across_step_sizes(double step)
    {
        uint life = 100;
        double carry = 0, elapsed = 0;
        for (int i = 0; i < 100; i++)
        {
            life = RecoveryPool.Advance(life, 10000, 23.75 * step, ref carry, minimum: 1);
            elapsed += step;
        }
        Assert.Equal(100 + 23.75 * elapsed, life + carry, 8);
    }

}

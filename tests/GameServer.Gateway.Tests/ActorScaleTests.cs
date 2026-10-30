namespace GameServer.Gateway.Tests;

public class ActorScaleTests
{
    [Theory]
    [InlineData(0, 0, 0, 100, 0)]
    public void Native_float_order_rounding_bounds_and_defined_domain_extremes(
        int raw, int capped, int permyriad, int limit, int expected) =>
        Assert.Equal(expected, ActorScale.Derive(raw, capped, permyriad, limit));

}

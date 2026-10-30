namespace GameServer.Gateway.Tests;

public class WardStateTests
{
    static int None(string _) => 0;

    [Fact]
    public void Converted_defences_keep_fractions_until_the_final_ward_value()
    {
        Assert.Equal(10u, WardState.Max(19m * .5m, 10));
        Assert.Equal(20u, WardState.Max(19m * .5m, 120));
    }

}

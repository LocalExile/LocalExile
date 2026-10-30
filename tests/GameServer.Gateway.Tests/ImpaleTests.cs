namespace GameServer.Gateway.Tests;

public class ImpaleTests
{
    [Fact]
    public void An_impale_stores_a_tenth_of_the_hits_physical_damage()
    {
        var impale = new ImpaleState();
        Assert.True(impale.Apply(120));
        Assert.Equal(1, impale.Stacks);
        Assert.Equal(12, impale.Bonus, 6);
    }

}

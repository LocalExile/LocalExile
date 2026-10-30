using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class DamageModelTests
{
    [Fact]
    public void Resistances_are_capped_and_floored()
    {
        Assert.Equal(75, DamageModel.Capped(90));
        Assert.Equal(75, DamageModel.Capped(75));
        Assert.Equal(0, DamageModel.Capped(0));
        Assert.Equal(-60, DamageModel.Capped(-60));
        Assert.Equal(-200, DamageModel.Capped(-300));
        Assert.Equal(90, DamageModel.Capped(100, 90));
        Assert.Equal(90, DamageModel.Capped(100, 200));
    }

}

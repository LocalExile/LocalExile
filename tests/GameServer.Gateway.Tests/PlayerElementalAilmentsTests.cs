using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PlayerElementalAilmentsTests
{
    static int None(string _) => 0;
    static DamagePacket Fire(int damage) => DamagePacket.Of(DamageType.Fire, damage, damage);

    [Fact]
    public void Strongest_ignite_is_integrated_through_expiry_and_the_weaker_longer_one_resumes()
    {
        var ailments = new PlayerElementalAilments();
        ailments.Apply(Fire(200), Fire(200), true, 1000, 0, None, new Random(1));
        ailments.Apply(Fire(100), Fire(100), true, 1000, 2, None, new Random(1));
        Assert.Equal(234 * 4 + 117 * 2, ailments.Advance(0, 10, None, 0, 75, false));
        Assert.Equal(0, ailments.Advance(10, 11, None, 0, 75, false));
        Assert.False(ailments.Snapshot(10).Ignite);
    }

}

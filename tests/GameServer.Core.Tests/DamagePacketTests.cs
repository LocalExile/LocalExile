using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class DamagePacketTests
{
    [Fact]
    public void A_packet_keeps_each_damage_type_separate()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 10, 20)
            .Add(DamageType.Fire, new DamageRange(3, 5));

        Assert.Equal(10, hit[DamageType.Physical].Min);
        Assert.Equal(20, hit[DamageType.Physical].Max);
        Assert.Equal(3, hit[DamageType.Fire].Min);
        Assert.Equal(5, hit[DamageType.Fire].Max);
        Assert.Equal(19, hit.AverageTotal);
    }

}

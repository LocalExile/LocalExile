using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class HorticraftingProtocolTests
{
    [Fact]
    public void Harvest_opens_its_own_native_window_without_a_bench_type_or_object_parameter()
    {
        Assert.Equal("007B3900000000000000000000000000000000",
            Convert.ToHexString(InstanceWaypoints.OpenUi(InstanceWaypoints.HorticraftingUi)));
        Assert.NotEqual(InstanceWaypoints.CraftingBenchUi, InstanceWaypoints.HorticraftingUi);
    }

}

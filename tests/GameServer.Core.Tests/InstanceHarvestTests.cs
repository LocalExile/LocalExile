using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceHarvestTests
{
    [Fact]
    public void Craft_request_is_a_four_byte_message_with_the_recipe_hash()
    {
        Assert.Equal(4, InstanceProtocol.KnownClientLength(InstanceHarvest.CraftId));
        Assert.Equal(0xa3e5, InstanceHarvest.ReadCraft([0x02, 0x74, 0xa3, 0xe5]));
    }

}

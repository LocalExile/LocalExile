using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DescendancyProtocolTests
{
    [Fact]
    public void The_passive_tree_carries_the_descendancy_row_before_its_node_lists()
    {
        byte[] message = InstanceEntities.CharacterPassives(90, 0x01, [0x2a, 0x29], descendancy: 6);
        Assert.Equal(InstanceEntities.CharacterPassivesId, (ushort)(message[0] << 8 | message[1]));
        Assert.Equal(90, message[8]);
        Assert.Equal(0x01, message[9]);
        Assert.Equal(6, message[10]);
        Assert.Equal(0, message[11]);
    }

    [Fact]
    public void The_descendancy_accept_is_a_three_byte_fixed_message()
    {
        Assert.Equal(InstanceWaypoints.DescendancyChosenLength,
            InstanceProtocol.KnownClientLength(InstanceWaypoints.DescendancyChosenId));
    }
}

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

    [Fact]
    public void Quick_move_item_0x00f8_is_eight_bytes()
    {
        byte[] msg = Convert.FromHexString("00F8" + "0000002A" + "01" + "01");
        var messages = InstanceProtocol.SplitClientMessages(msg, out var partial, out var unknown);
        Assert.Equal(InstanceHarvest.QuickMoveItemId, Assert.Single(messages).Id);
        Assert.Equal(new InstanceItems.LeagueWindowMove(0x2au, true, true), InstanceHarvest.ReadQuickMoveItem(messages[0].Bytes));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }
}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class HideoutSelectionTests
{
    [Theory]
    [InlineData(0x00, 0x1d, 29, false)]
    public void Selection_decodes_the_native_row_and_guild_bit(byte high, byte low, int row, bool guild)
    {
        Assert.Equal((row, guild), InstanceHideouts.ReadSelect(new byte[] { 2, 0x54, high, low }));
        var messages = InstanceProtocol.SplitClientMessages(new byte[] { 2, 0x54, high, low, 2, 0x54, 0, 0 }, out var partial, out var unknown);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(InstanceHideouts.SelectId, m.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }
}

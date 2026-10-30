using System.Text.Json;
using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GracePeriodTests
{
    [Theory]
    [InlineData(false, false, true)]
    public void Entry_grants_protection_only_in_combat_areas(bool town, bool hideout, bool expected)
    {
        var grace = new GracePeriod();
        Assert.Equal(expected, grace.Start(12, (100, 200), 7, town, hideout));
        Assert.Equal(expected, grace.Protects(12, (100, 200)));
    }

}

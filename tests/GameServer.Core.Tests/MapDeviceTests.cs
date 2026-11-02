using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class MapDeviceTests
{
    [Fact]
    public void Activate_is_0xd8_with_a_big_endian_target_and_an_index()
    {
        byte[] m = Convert.FromHexString("00D8012A03");
        Assert.Equal(InstanceMapDevice.ActivateLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.ActivateId));
        Assert.Equal(((ushort)0x012a, (byte)3), InstanceMapDevice.ReadActivate(m));
        var split = InstanceProtocol.SplitClientMessages(Convert.FromHexString("00D8000000"), out var partial, out var unknown);
        Assert.Equal([InstanceMapDevice.ActivateId], split.Select(s => s.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

}

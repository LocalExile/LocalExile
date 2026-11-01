using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public class ResyncTests
{
    [Fact]
    public void Resync_is_a_compact_count_of_big_endian_object_and_cell_triples_with_the_player_first() =>
        Assert.Equal("0128" + "02" + "00001000" + "0000014A" + "00000105" + "00003004" + "0000012C" + "00000100",
            Convert.ToHexString(InstanceProtocol.Resync([(0x1000, 330, 261), (0x3004, 300, 256)])));
}

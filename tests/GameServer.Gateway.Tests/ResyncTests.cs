using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public class ResyncTests
{
    [Fact]
    public void Resync_is_a_compact_count_of_big_endian_object_and_cell_triples_with_the_player_first() =>
        Assert.Equal("0128" + "02" + "00001000" + "0000014A" + "00000105" + "00003004" + "0000012C" + "00000100",
            Convert.ToHexString(InstanceProtocol.Resync([(0x1000, 330, 261), (0x3004, 300, 256)])));

    [Fact]
    public void A_corpse_stops_blocking_with_the_object_id_as_its_order_key() =>
        Assert.Equal("02B0" + "000000CE" + "000000CE" + "7000" + "00" + "00",
            Convert.ToHexString(InstanceProtocol.StopBlocking(0xCE)));
}

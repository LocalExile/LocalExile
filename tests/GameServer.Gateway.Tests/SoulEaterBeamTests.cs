using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The official server's Soul Eater line: the ServerBeamEffect blob, captured from 3.29.3c (row 0,
/// flags 0x0C97, world endpoints as grid cells times ten, both ends' object ids), as our client reads it.</summary>
public sealed class SoulEaterBeamTests
{
    [Fact]
    public void Soul_beam_blob_is_the_official_packet()
    {
        var blob = new BeamEffectObject(1421, 612, 0x452, 14099.1f, 5936.7f, 0x4B9).Blob();
        Assert.Equal(
            "00008D050000640200000000000000000000000100970C0000085E460040BF455204000000000000664C5C46" +
            "9A85B945B90400000000000000",
            Convert.ToHexString(blob));
    }
}

using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

/// <summary>The Atlas's completed maps (21-hideouts.md §4): the nodes from AtlasNode.dat and the S→C 0x1a3 / 0x1a4 messages.</summary>
public sealed class AtlasProgressTests
{
    static AtlasProgress.Node Chateau => AtlasProgress.Find("Chateau")!;

    [Fact]
    public void MessagesCarryTheNodesRowMasksAndHash()
    {
        byte[] full = AtlasProgress.FullMessage(new HashSet<string> { "MapWorldsChateau" });
        Assert.Equal(7 + 2 * 22, full.Length);
        Assert.Equal(new byte[] { 0x01, 0xa3, 0, 0, 0, 0x03, 0 }, full[..7]);
        // The completed map's row is set in both masks. Its byte also carries the neighbouring on-Atlas rows, so the
        // bit itself is what is checked, not the whole byte.
        Assert.NotEqual(0, full[7 + 97 / 8] & (1 << (97 & 7)));
        Assert.NotEqual(0, full[7 + 22 + 97 / 8] & (1 << (97 & 7)));
        // Every on-Atlas row goes into both masks (the hidden rows and the voidstone slots included), so the
        // completed mask is as full as the revealed one and the bottom citadels stay connected.
        Assert.Equal(full[7..29], full[29..]);
        Assert.Equal(new byte[] { 0x01, 0xa4, 0x9a, 0x23, 0, 0, 0x02 }, AtlasProgress.NodeMessage(Chateau));
        byte[] empty = AtlasProgress.EmptyMessage();
        Assert.Equal(full.Length, empty.Length);
        Assert.Equal(full[..7], empty[..7]);
        Assert.All(empty[7..], b => Assert.Equal(0, b));
    }
}

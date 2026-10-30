using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// 3.29 socket colours: sockets are white by default, with an item-level (and, for Chromatic, quality) chance to roll a
// colour proportional to the base's attribute requirements. New sockets from currency and the bench stay white.
public class SocketColoursTests
{
    [Fact]
    public void New_items_generate_two_linked_sockets_and_most_are_white()
    {
        var (catalog, _, _, _) = BenchCraftingTests.Setup.Value;
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;

        var (sockets, links) = catalog.DefaultSockets(type, itemLevel: 1);
        Assert.Equal(2, sockets.Count);
        Assert.Equal(new byte[] { 2 }, links);

        // 5% per socket at item level 1: a few colours over 4000 sockets, never most of them.
        int coloured = Enumerable.Range(0, 2000)
            .Sum(_ => catalog.DefaultSockets(type, itemLevel: 1).Sockets.Count(s => SocketColours.IsColoured(s.Colour)));
        Assert.InRange(coloured, 1, 800);
    }

}

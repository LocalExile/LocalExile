using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CurrencyEffectsTests
{
    [Theory]
    [InlineData(2)]
    public void Fusing_changes_only_links_and_keeps_socketed_gems(int count)
    {
        var before = Boots(Enumerable.Repeat(SocketColour.White, count).ToArray()) with
        {
            ItemLevel = 1, Quality = 20, Rarity = ItemRarity.Rare,
            Links = Enumerable.Repeat((byte)1, count).ToArray(),
        };
        var random = new Random(9);
        for (int i = 0; i < 100; i++)
        {
            var after = CurrencyEffects.Fusing(before, SocketBase("Body Armour", 2, 4), random)!;
            Assert.NotNull(after);
            Assert.False(before.Links!.SequenceEqual(after.Links!));
            Assert.Same(before.Sockets, after.Sockets);
            Assert.NotNull(after.Sockets[0].Gem);
            Assert.Equal(count, after.Links!.Sum(n => n));
            Assert.All(after.Links!, n => Assert.InRange(n, (byte)1, (byte)count));
            Assert.Equal(before, after with { Links = before.Links });
        }
    }

    static ItemCatalog.BaseType SocketBase(string itemClass, byte width = 2, byte height = 3) =>
        new(0, "test", "test", 0x1234, width, height, 0x10, ["Base", "Sockets"], 0, Class: itemClass);

    static Item Boots(params SocketColour[] colours) => new(0x1234, ["Base", "Sockets"], 0x10)
    {
        Sockets = [.. colours.Select((c, i) => new ItemSocket(c, i == 0 ? new Item(0x99, ["Base"], 0x20) : null))],
        Links = [(byte)colours.Length],
    };

}

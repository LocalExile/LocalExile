using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A player trade's inventory side: each player's offered items stay reserved in their cells until the trade ends, then
// go to the other player in one save per side, after both sides were checked to fit.
public class PlayerTradeTests : IDisposable
{
    const uint WandType = 0xd9b1a12f, OrbType = 0x1234abcd;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(OrbType, "Orb", StackSize: 40));
    readonly string _a = $"TraderA{Guid.NewGuid():N}"[..20], _b = $"TraderB{Guid.NewGuid():N}"[..20];
    const byte Main = PlayerInventories.MainInventory;

    public PlayerTradeTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    PlayerInventories Items(string character) => PlayerInventories.For(character, new ItemCatalog(_folder));
    PlayerInventories Reload(string character)
    {
        PlayerInventories.Forget(character);
        return Items(character);
    }

    static Item Wand() => new(WandType, ["Base"], 0x40a1);
    static Item Orbs(int n) => new(OrbType, ["Base", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash == OrbType ? new(1, 1, "StackableCurrency", 40) : new(1, 3, "Wand");

    [Fact]
    public void Cancelling_keeps_every_offered_item_in_its_cell()
    {
        var a = Items(_a);
        var wand = a.Add(Main, 3, 1, Wand());
        a.Offer(Main, wand.Id, 0, 0, Shapes);

        var back = Assert.Single(a.CancelOffers());

        Assert.Equal(wand, back.Item);
        Assert.Contains(Reload(_a).Items(Main), i => i.Id == wand.Id && i.X == 3 && i.Y == 1);
    }

}

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
    public void Both_offers_change_owners_in_one_save_each()
    {
        var a = Items(_a);
        var b = Items(_b);
        var wand = a.Add(Main, 0, 0, Wand());
        var orbs = b.Add(Main, 0, 0, Orbs(7));
        a.Offer(Main, wand.Id, 0, 0, Shapes);
        b.Offer(Main, orbs.Id, 0, 0, Shapes);
        List<Item> fromA = [.. a.Offered().Select(i => i.Item)], fromB = [.. b.Offered().Select(i => i.Item)];

        Assert.True(a.CanExchange(fromB, Shapes));
        Assert.True(b.CanExchange(fromA, Shapes));
        Assert.NotNull(a.Exchange(fromB, Shapes));
        Assert.NotNull(b.Exchange(fromA, Shapes));

        var againA = Reload(_a);
        var againB = Reload(_b);
        Assert.Equal(0, againA.Items(Main).Count(i => i.Item.TypeHash == WandType));
        Assert.Equal(7, againA.Count(Main, OrbType));
        Assert.Single(againB.Items(Main), i => i.Item.TypeHash == WandType);
        Assert.Equal(0, againB.Count(Main, OrbType));
        Assert.Empty(againA.Offered());
        Assert.Empty(againB.Offered());
    }

    [Fact]
    public void A_side_that_offers_nothing_still_receives()
    {
        var a = Items(_a);
        var b = Items(_b);
        var wand = a.Add(Main, 0, 0, Wand());
        a.Offer(Main, wand.Id, 0, 0, Shapes);
        List<Item> fromA = [.. a.Offered().Select(i => i.Item)];

        Assert.NotNull(a.Exchange([], Shapes));
        Assert.NotNull(b.Exchange(fromA, Shapes));

        Assert.Empty(Reload(_a).Items(Main));
        Assert.Single(Reload(_b).Items(Main), i => i.Item.TypeHash == WandType);
    }

    [Fact]
    public void Items_that_do_not_fit_change_nothing()
    {
        var a = Items(_a);
        var size = InstanceInventories.PlayerDefaults[Main];
        for (byte x = 0; x < size.Width; x++)
            for (byte y = 0; y + 3 <= size.Height; y += 3)
                a.Add(Main, x, y, Wand());
        int before = a.Items(Main).Count;
        List<Item> incoming = [Wand()];

        Assert.False(a.CanExchange(incoming, Shapes));
        Assert.Null(a.Exchange(incoming, Shapes));
        Assert.Equal(before, Reload(_a).Items(Main).Count);
    }

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

    [Fact]
    public void Social_requests_are_framed_and_read()
    {
        byte[] trade = [0x00, 0xba, 0x20, 0x00, 0x00, 0x01, 0x00, 0x1b];
        var split = InstanceProtocol.SplitClientMessages(trade, out var partial, out var unknown);
        Assert.Equal(InstanceSocial.TradeRequestId, split[0].Id);
        Assert.Equal(0x20000001u, InstanceSocial.ReadTradeRequest(split[0].Bytes));
        Assert.Empty(unknown);
        Assert.Equal([0x00, 0x1b], partial);

        byte[] accept = [0x00, 0xb0, 0x20, 0x00, 0x00, 0x03];
        Assert.Equal(0x20000003u, InstanceSocial.ReadTradeRequestAccept(InstanceProtocol.SplitClientMessages(accept, out _, out _)[0].Bytes));

        // Live 2026-10-28: "Invite to Party" and "Add Friend" on the character Admin.
        byte[] party = Convert.FromHexString("0093000541646D696E");
        byte[] friend = Convert.FromHexString("0080000541646D696E0000");
        var both = InstanceProtocol.SplitClientMessages([.. party, .. friend], out _, out var rest);
        Assert.Empty(rest);
        Assert.Equal("Admin", InstanceSocial.ReadPartyInvite(both[0].Bytes));
        Assert.Equal(("Admin", (ushort)0), InstanceSocial.ReadFriendAdd(both[1].Bytes));
    }

    [Fact]
    public void A_known_player_carries_the_character_section()
    {
        // wstr account, wstr, flags 0x04 (character data), wstr character, u8, u8, u16 BE level, u16, wstr, u8.
        Assert.Equal(Convert.FromHexString("00A7" + "0004" + "74657374" + "0000" + "04" + "0005" + "4465644F4B" + "0000" + "0064" + "0000" + "0000" + "00"),
            InstanceSocial.KnownPlayer("test", "DedOK", 100));
    }
}

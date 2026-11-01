using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class ItemDefenceBaseRollTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    public void Armour_component_writes_the_exact_raw_roll_in_one_byte(byte roll)
    {
        var item = new Item(1, ["Armour"], 0) { DefenceBaseRoll = roll };
        Assert.Equal(7, item.Payload().Length);
        Assert.Equal(roll, item.Payload()[6]);
        Assert.Equal(9, item.Wire().Length);
        Assert.Equal(7, item.Wire()[1]);
        Assert.Equal(roll, item.Wire()[8]);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(255)]
    public void Unrepresentable_server_rolls_are_refused(byte roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Item(1, ["Armour"], 0) { DefenceBaseRoll = roll });
    }

    [Fact]
    public void A_legacy_default_item_still_sends_the_minimum_roll()
    {
        Assert.Equal(0, new Item(1, ["Armour"], 0).Payload()[6]);
    }

    [Fact]
    public void Ordinary_item_mutations_copy_the_defence_roll()
    {
        var before = new Item(1, ["Armour", "Quality"], 0) { DefenceBaseRoll = 16 };
        var after = before with { Quality = 20, Rarity = ItemRarity.Rare };
        Assert.Equal(16, after.DefenceBaseRoll);
        Assert.Equal(16, after.Payload()[6]);
        Assert.Equal(20, after.Payload()[7]);
    }
}

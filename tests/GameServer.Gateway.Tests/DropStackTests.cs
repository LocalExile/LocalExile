namespace GameServer.Gateway.Tests;

// One monster's drop event puts stackable items of one type on the ground as stacks, not one world item per unit.
public sealed class DropStackTests
{
    [Fact]
    public void One_drop_event_combines_stackable_items_into_full_stacks()
    {
        var items = new ItemCatalog("data");
        var alteration = items.Find("Metadata/Items/Currency/CurrencyUpgradeToMagic")!;
        var greaves = items.Find("Iron Greaves")!;
        Assert.True(alteration.MaxStack > 1);

        var one = ItemCatalog.Create(alteration);
        var drops = new List<ItemDrops.Drop>();
        for (int i = 0; i < alteration.MaxStack + 2; i++) drops.Add(new(one, alteration));
        drops.Add(new(ItemCatalog.Create(greaves), greaves));

        var stacked = ItemDrops.Stacked(drops);
        var currency = stacked.Where(d => d.Type == alteration).ToList();
        Assert.Equal(2, currency.Count);
        Assert.Equal(alteration.MaxStack, currency[0].Item.StackSize);
        Assert.Equal(2, currency[1].Item.StackSize);
        Assert.Equal(alteration.MaxStack + 2, currency.Sum(d => d.Item.StackSize));
        Assert.Single(stacked, d => d.Type == greaves);

        // A drop that already carries a stack joins the same stack.
        var three = new ItemDrops.Drop(one with { StackSize = 3 }, alteration);
        Assert.Equal(5, ItemDrops.Stacked([three, new(one, alteration), new(one, alteration)]).Single().Item.StackSize);
    }
}

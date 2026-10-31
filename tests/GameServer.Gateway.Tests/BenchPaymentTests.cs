using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class BenchPaymentTests : IDisposable
{
    readonly string name = "Bench" + Guid.NewGuid().ToString("N");
    const byte Main = PlayerInventories.MainInventory, Slot = PlayerInventories.CraftingBench;
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static BenchCrafting Bench => BenchCraftingTests.Setup.Value.Bench;
    PlayerInventories Items => PlayerInventories.For(name, Catalog);
    AccountStash Stash => AccountStash.For(name + "Account", Catalog);
    public BenchPaymentTests() => TestSaves.Ensure();
    public void Dispose() => Saves.FailWrite = null;
    static PlayerInventories.Shape? Shape(Item i) => Catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
    static Item Currency(uint hash, int count) => ItemCatalog.Create(Catalog.ByHash(hash)!) with { StackSize = (ushort)count };
    void Deposit(uint hash, int count, int tab = 0) => Assert.Equal(0, Stash.AddStacked(tab, Currency(hash, count), Shape, out _)!.Left);
    static BenchCrafting.Recipe Recipe => Bench.All.First(r => r.Mod is not null && r.Classes.Contains(25) && r.Costs.Count == 1 && r.Costs[0].Amount >= 6);
    InventoryItem Target(string itemClass = "Helmet") => Items.Add(Slot, 0, 0,
        ItemCatalog.Create(BenchCraftingTests.Setup.Value.Types.Single(t => t.Class == itemClass)) with { ItemLevel = 100 });

    [Fact]
    public void Split_inventory_and_multiple_stash_tabs_pay_exactly_the_price_and_persist_the_craft()
    {
        var target = Target();
        var recipe = Recipe;
        var (currency, amount) = recipe.Costs.Single();
        Items.Add(Main, 0, 0, Currency(currency, 2));
        Deposit(currency, 2);
        Deposit(currency, amount + 5, 1);
        Assert.NotNull(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, new Random(1)));
        Assert.Equal(0, Items.Count(Main, currency));
        Assert.Equal(9, Stash.CountOf(currency));
        var saved = CharacterSaves.Load(name)!.Inventories[Slot].Single(i => i.Id == target.Id);
        Assert.Single(saved.Item.Mods!, m => m.Id == recipe.Mod!.Id);
    }

}

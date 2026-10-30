using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class CurrencyStashStackTests : IDisposable
{
    const uint Lifeforce = 0x12340001, Scroll = 0x12340002;
    const int Tab = StashRulesTests.Currency;
    readonly string _folder = TestSaves.Catalog(new(Lifeforce, "Metadata/Items/Currency/HarvestSeedGreen", StackSize: 50000),
        new(Scroll, "Scroll", StackSize: 40));
    readonly string _account = "currency-" + Guid.NewGuid().ToString("N");
    readonly ItemCatalog _catalog;

    public CurrencyStashStackTests()
    {
        TestSaves.Ensure();
        void Write(string name, object rows) => File.WriteAllText(Path.Combine(_folder, "tables", name + ".json"),
            JsonSerializer.Serialize(rows));
        foreach (string name in new[] { "StashTabAffinityByBaseItemType", "StashtabAffinityByItemClassCategory",
            "ItemClasses", "EssenceStashTabLayout", "FragmentStashTabLayout", "DelveStashTabLayout",
            "BlightStashTabLayout", "DeliriumStashTabLayout", "DivinationCardStashTabLayout" }) Write(name, Array.Empty<object>());
        Write("StashTabAffinities", new[] { new { Id = 3, __col2 = new[] { 3, 0 } } });
        // The layout Id need not match the base Id: StoredItem is the authoritative row reference.
        Write("CurrencyStashTabLayout", new[] {
            new { Id = "HarvestSlot", StoredItem = 0, FirstSlotIndex = 99 },
            new { Id = "ScrollSlot", StoredItem = 1, FirstSlotIndex = 1 } });
        Write("CurrencyItems", new[] {
            new { BaseItemTypesKey = 0, StackSize = 50000, CurrencyTab_StackSize = 50000, FullStack_BaseItemTypesKey = (int?)null },
            new { BaseItemTypesKey = 1, StackSize = 40, CurrencyTab_StackSize = 10000, FullStack_BaseItemTypesKey = (int?)null } });
        _catalog = new ItemCatalog(_folder);
    }

    PlayerInventories.Shape? Shape(Item item) => _catalog.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
    AccountStash Open() => AccountStash.For(_account, _catalog);

    [Fact]
    public void Currency_limits_come_from_the_table_and_grid_tabs_keep_the_item_limit()
    {
        Assert.Equal<ushort?>(50000, _catalog.Stash.MaxStack(StashRules.Currency, Lifeforce));
        Assert.Equal<ushort?>(10000, _catalog.Stash.MaxStack(StashRules.Currency, Scroll));
        Assert.Equal<ushort?>(5000, _catalog.Stash.MaxStack(StashRules.Essence, Scroll));
        Assert.Null(_catalog.Stash.MaxStack(0, Lifeforce));
    }

    public void Dispose()
    {
        AccountStash.Forget(_account);
        Directory.Delete(_folder, recursive: true);
    }
}

using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// What the special stash tabs hold (StashRules): a map tab only maps, a currency tab each currency in its own slot; items
// a tab can't show come out of it at load.
public class StashRulesTests : IDisposable
{
    internal const uint WandType = 0xd9b1a12f, ScrollType = 0x5c10aa01, MapType = 0x3a9b0001, OrbType = 0x1234abcd;
    internal const int Currency = 7, Map = 10;   // the starter tabs' indexes of those types (after the four normal tabs)
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"), new TestSaves.Type(OrbType, "Orb", StackSize: 20));
    readonly string _account = $"Rules{Guid.NewGuid():N}"[..16];

    public StashRulesTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Scrolls have the currency affinity and slot 1 (a currency tab slot holds 50 of them), maps the map
    /// affinity; orbs and wands neither. The currency tab's crafting slot takes anything; two spare slots (the first two of
    /// Generic1–14) take its overflow.</summary>
    internal static StashRules Rules() => StashRules.For(
        new() { [ScrollType] = [3], [MapType] = [5] },
        new() { [3] = [0, 1, 7, 10, 3], [5] = [0, 1, 7, 10, 5] },
        new() { [StashRules.Currency] = new() { [ScrollType] = 1 } },
        new() { [ScrollType] = 50 },
        new() { [StashRules.Currency] = [CraftingSlot] },
        new() { [StashRules.Currency] = [SpareSlot1, SpareSlot2] });

    internal const int CraftingSlot = 28;   // CurrencyStashTabLayout CraftingSlot
    internal const int SpareSlot1 = 30, SpareSlot2 = 31;   // Generic1, Generic2

    internal static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash switch
    {
        ScrollType => new(1, 1, "StackableCurrency", 40),
        OrbType => new(1, 1, "StackableCurrency", 20),
        MapType => new(1, 1, "Map"),
        _ => new(1, 3, "Wand"),
    };

    internal static Item Wand() => new(WandType, ["Base"], 0x40a1);
    internal static Item Scrolls(int n) => new(ScrollType, ["Base", "Stack"], 0x2222) { StackSize = (ushort)n };
    internal static Item MapItem() => new(MapType, ["Base"], 0x3333);

    AccountStash Open(StashRules? rules = null) => AccountStash.For(_account, new ItemCatalog(_folder) { Stash = rules ?? Rules() });

    [Fact]
    public void The_map_tab_takes_nothing_until_its_layout_is_known()
    {
        // The client lists a map tab's maps by tier, not at grid cells: anything saved there would not be shown.
        var stash = Open();

        Assert.Null(stash.Place(Map, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shapes, out string why));
        Assert.Contains("doesn't hold", why);
        Assert.Null(stash.Place(Map, 0, 0, new InventoryItem(2, 0, 0, MapItem()), Shapes, out _));
        Assert.True(stash.Holds(0, MapType));   // a normal tab holds maps, and anything else
        Assert.False(Open(new StashRules()).Holds(Map, MapType));   // even without the tables
    }

    static string? FindData()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "tables", "StashTabAffinities.json"))) return Path.Combine(dir.FullName, "data");
        return null;
    }
}

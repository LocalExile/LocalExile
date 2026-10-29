using GameServer.Core.Protocol.Instance;
using static GameServer.Gateway.Tests.StashRulesTests;

namespace GameServer.Gateway.Tests;

// /item_tab: every item type with a slot of its own on a slotted tab gets up to the asked amount more, each capped by
// what its slot holds (one of an item that doesn't stack), in one save.
public class ItemTabCommandTests : IDisposable
{
    const int Normal = 0;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"), new TestSaves.Type(OrbType, "Orb", StackSize: 20));
    readonly string _account = $"ItemTab{Guid.NewGuid():N}"[..16];

    public ItemTabCommandTests() => TestSaves.Ensure();

    public void Dispose()
    {
        AccountStash.Forget(_account);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // Scrolls: currency-tab limit 50; orbs: no item-specific limit (StashRules.SlotStack); a wand doesn't stack.
    // As StashRules.Load does, every slotted type has the tab's affinity, so a reload keeps it there.
    static StashRules Rules() => StashRules.For(
        new() { [ScrollType] = [3], [OrbType] = [3], [WandType] = [3] },
        new() { [3] = [0, 1, 7, 10, StashRules.Currency] },
        new() { [StashRules.Currency] = new() { [ScrollType] = 1, [OrbType] = 2, [WandType] = 3 } },
        new() { [ScrollType] = 50 });

    AccountStash Open() => AccountStash.For(_account, new ItemCatalog(_folder) { Stash = Rules() });

    static Item? Create(uint type) => type switch
    {
        ScrollType => Scrolls(1),
        OrbType => new Item(OrbType, ["Base", "Stack"], 0x1) { StackSize = 1 },
        WandType => Wand(),
        _ => null,
    };

    static int StackAt(AccountStash stash, int slot) =>
        stash.Inventory(Currency).Items.Where(i => i.X == slot && i.Y == 0).Sum(i => i.Item.StackSize);

    [Fact]
    public void Each_slot_gets_the_amount_up_to_what_it_holds_and_it_survives_a_reload()
    {
        var stash = Open();

        var first = stash.FillSlots(Currency, 30, Create, Shapes, out _)!;
        Assert.Equal(3, first.Added.Count);
        Assert.Equal((30, 30, 1), (StackAt(stash, 1), StackAt(stash, 2), StackAt(stash, 3)));

        var second = stash.FillSlots(Currency, 1000, Create, Shapes, out _)!;
        Assert.Equal(2, second.Changed.Count);
        Assert.Empty(second.Added);
        Assert.Equal(1, second.Full);   // the wand: one is all its slot takes
        Assert.Equal((50, 1030, 1), (StackAt(stash, 1), StackAt(stash, 2), StackAt(stash, 3)));
        Assert.Equal(first.Added.Single(i => i.X == 2).Id, second.Changed.Single(i => i.X == 2).Id);   // grown in place

        Assert.Equal(2, stash.FillSlots(Currency, 1000, Create, Shapes, out _)!.Full);
        AccountStash.Forget(_account);
        var reloaded = Open();
        Assert.Equal((50, 2030, 1), (StackAt(reloaded, 1), StackAt(reloaded, 2), StackAt(reloaded, 3)));
    }

    [Fact]
    public void Types_the_server_cannot_create_are_skipped()
    {
        var stash = Open();

        var fill = stash.FillSlots(Currency, 5, t => t == OrbType ? null : Create(t), Shapes, out _)!;

        Assert.Equal(1, fill.Skipped);
        Assert.Equal(0, StackAt(stash, 2));
        Assert.Equal(5, StackAt(stash, 1));
    }

    [Fact]
    public void A_slot_beyond_the_tab_cells_is_skipped_instead_of_wrapping()
    {
        // The currency tab has 142 × 4 cells; slot 142 × 4 + 1 would wrap into slot 1.
        var rules = StashRules.For(new() { [ScrollType] = [3], [OrbType] = [3] }, new() { [3] = [StashRules.Currency] },
            new() { [StashRules.Currency] = new() { [ScrollType] = 1, [OrbType] = 142 * 4 + 1 } });
        var stash = AccountStash.For(_account, new ItemCatalog(_folder) { Stash = rules });

        var fill = stash.FillSlots(Currency, 5, Create, Shapes, out _)!;

        Assert.Equal(1, fill.Skipped);
        Assert.Equal(5, StackAt(stash, 1));   // not 10: the orb did not wrap into slot 1
    }

    [Fact]
    public void Cell_fill_adds_each_item_once_into_free_cells_and_reports_no_room()
    {
        var stash = Open();
        static string Key(Item i) => $"{i.TypeHash:x8}:{i.GemVariant}";
        Item Gem(int variant) => MapItem() with { GemVariant = variant };

        var first = stash.FillCells(Normal, [Gem(0), Gem(1), Gem(0)], Key, Shapes, out _)!;
        Assert.Equal(2, first.Added.Count);
        Assert.Equal(1, first.Full);   // the second Gem(0) is already there
        Assert.Equal([(0, 0), (0, 1)], first.Added.Select(i => ((int)i.X, (int)i.Y)));

        var again = stash.FillCells(Normal, Enumerable.Range(0, 200).Select(Gem), Key, Shapes, out _)!;
        Assert.Equal(2, again.Full);
        Assert.Equal(12 * 12 - 2, again.Added.Count);   // a 12 × 12 tab
        Assert.Equal(200 - 2 - (12 * 12 - 2), again.Skipped);
        AccountStash.Forget(_account);
        Assert.Equal(12 * 12, Open().Inventory(Normal).Items.Count);
    }

    [Fact]
    public void Tabs_without_a_slot_per_type_are_refused_unchanged()
    {
        var stash = Open();

        Assert.Null(stash.FillSlots(Normal, 10, Create, Shapes, out string why));
        Assert.Contains("no slot for each item type", why);
        Assert.Null(stash.FillSlots(Map, 10, Create, Shapes, out why));
        Assert.Contains("doesn't show", why);
        Assert.Empty(stash.Inventory(Normal).Items);
    }

    // A slot past the advertised width draws over the tab (blight oils at slots 12 and 79–82 in the 12 × 12 default).
    [Theory]
    [InlineData(StashRules.Currency), InlineData(StashRules.Essence), InlineData(StashRules.Fragment), InlineData(StashRules.Delve),
     InlineData(StashRules.Blight), InlineData(StashRules.Delirium), InlineData(StashRules.Divination)]
    public void Every_real_slot_fits_the_published_tab_size(byte tabType)
    {
        var slots = Program.Items.Stash.Slots(tabType);
        Assert.NotEmpty(slots);
        Assert.True(AccountStash.Types.TryGetValue(tabType, out var size), $"no size for StashType {tabType}");
        // Slot n is cell (n mod width, n / width): the divination tab's 470 cards fill both rows of its 255 × 2.
        Assert.All(slots.Values, s => Assert.True(s < size.Width * size.Height, $"slot {s} outside {size.Name} {size.Width} x {size.Height}"));
    }

    [Fact]
    public void Divination_cards_past_the_first_row_go_into_the_second_and_fill_to_a_complete_set()
    {
        const int Divination = 18;   // the starter tabs' divination tab
        var rules = StashRules.For(new() { [ScrollType] = [6], [OrbType] = [6] }, new() { [6] = [StashRules.Divination] },
            new() { [StashRules.Divination] = new() { [ScrollType] = 3, [OrbType] = 255 + 4 } });
        var stash = AccountStash.For(_account, new ItemCatalog(_folder) { Stash = rules });
        Assert.Equal("DivinationCardStash", stash.TypeOf(Divination));

        var fill = stash.FillSlots(Divination, 1000, Create, Shapes, out _)!;

        var cells = fill.Added.ToDictionary(i => i.Item.TypeHash, i => (i.X, i.Y, (int)i.Item.StackSize));
        Assert.Equal((3, 0, 40), cells[ScrollType]);   // a complete set: the card's own stack size, not the tab's 5000
        Assert.Equal((4, 1, 20), cells[OrbType]);
    }

    [Fact]
    public void The_tab_is_found_by_its_name_or_its_only_type()
    {
        var stash = Open();

        Assert.Equal(Currency, Program.ItemTabOf(stash, "currency"));
        Assert.Equal(Currency, Program.ItemTabOf(stash, "CurrencyStash"));
        Assert.Equal(Map, Program.ItemTabOf(stash, "MAP"));
        Assert.Null(Program.ItemTabOf(stash, "Normal"));   // four NormalStash tabs: which one is ambiguous
        Assert.Null(Program.ItemTabOf(stash, "nothing like it"));
    }
}

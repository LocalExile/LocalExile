namespace GameServer.Gateway.Tests;

/// <summary>
/// The cosmetics picker (CosmeticPicker.cs): the shop's portal effects as tabs of the vendor window, one effect per tab
/// because the tab strip is the only place the client draws our text. No game data here — the effects are made up, so
/// the layout and the worn mark are what is tested.
/// </summary>
public class CosmeticPickerTests
{
    static readonly ItemCatalog.BaseType Scroll = new(0, "Metadata/Items/Currency/CurrencyPortal", "Portal Scroll", 0x1234,
        1, 1, 1, ["Base", "Stack", "Mods"], 0);

    static List<CosmeticCatalog.Cosmetic> Effects(int n) =>
        [.. Enumerable.Range(1, n).Select(i =>
            new CosmeticCatalog.Cosmetic($"Effect {i:00} Portal Effect", $"Effect {i:00} Portal Effect", $"Metadata/rig{i}.ao", i))];

    static CosmeticPicker Build(int n, ushort worn = 0)
    {
        uint next = 100;
        return CosmeticPicker.Build(Effects(n), Scroll, () => next++, worn);
    }

    [Fact]
    public void Every_effect_gets_its_own_tab_after_the_off_tab()
    {
        var picker = Build(50);
        Assert.Equal(51, picker.EntryCount);
        Assert.Equal(51, picker.Pages.Count);
        Assert.All(picker.Pages, p => Assert.Single(p.Entries));
        Assert.Equal("Off", picker.Pages[0].Name);
        Assert.Equal(CosmeticPicker.FirstTabId, CosmeticPicker.TabId(0));
        // Every entry has its own item id: the window looks each click up by it.
        Assert.Equal(51, picker.Pages.SelectMany(p => p.Entries).Select(e => e.ItemId).Distinct().Count());
    }

    [Fact]
    public void A_tab_reads_the_effect_name_without_the_suffix_every_shop_name_ends_with()
    {
        var picker = Build(50);
        Assert.Equal(["Off", "Effect 01", "Effect 02"], picker.Pages.Take(3).Select(p => p.Name));
        Assert.Equal("Effect 50", picker.Pages[^1].Name);
    }

    [Fact]
    public void An_entry_sits_in_the_grid_and_applies_its_variation()
    {
        var picker = Build(30);
        var entry = picker.Pages[6].Entries[0];
        Assert.Equal(((byte)0, (byte)0), (entry.X, entry.Y));
        Assert.Equal((ushort)6, entry.Variation);          // the effect's own Index
        Assert.True(picker.Apply(entry.ItemId));
        Assert.Equal((ushort)6, picker.Worn);
        Assert.False(picker.Apply(entry.ItemId));          // the one already in use: nothing to redraw
        Assert.Equal(0, picker.Find(9999)?.Variation ?? 0);
        Assert.Null(picker.Find(9999));
    }

    [Fact]
    public void The_tab_of_the_effect_in_use_says_worn()
    {
        var picker = Build(30, worn: 0);
        Assert.Equal("Off (worn)", picker.Label(picker.Pages[0]));
        Assert.Equal("Effect 01", picker.Label(picker.Pages[1]));

        var entry = picker.Pages[3].Entries[0];
        picker.Apply(entry.ItemId);
        Assert.Equal("Effect 03 (worn)", picker.Label(picker.Pages[3]));
        Assert.Equal("Off", picker.Label(picker.Pages[0]));
        Assert.Equal(entry.ItemId, picker.Items(picker.Pages[3]).Single().Id);
    }

    [Fact]
    public void A_name_several_variations_share_gets_their_number()
    {
        List<CosmeticCatalog.Cosmetic> effects =
        [
            new("Orion Portal Effect", "Orion Portal Effect", "a.ao", 1),
            new("Orion Portal Effect", "Orion Portal Effect", "b.ao", 2),
        ];
        uint next = 1;
        var picker = CosmeticPicker.Build(effects, Scroll, () => next++, 0);
        Assert.Equal(["Orion #1", "Orion #2"], picker.Pages.Skip(1).Select(p => p.Name));
    }

    [Fact]
    public void The_variation_finds_its_entry_and_a_page_carries_its_own_items()
    {
        var picker = Build(30);
        var entry = picker.Pages[12].Entries[0];
        Assert.Equal(entry, picker.EntryOf(entry.Variation));
        Assert.Equal(picker.Pages[0].Entries[0], picker.EntryOf(0));
        var item = Assert.Single(picker.Items(picker.Pages[12]));
        Assert.Equal((entry.ItemId, Scroll.Hash), (item.Id, item.Item.TypeHash));
    }
}

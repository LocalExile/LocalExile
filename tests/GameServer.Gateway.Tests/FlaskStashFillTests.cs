using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FlaskStashFillTests
{
    [Fact]
    public void Fill_preserves_existing_flasks_and_reloads_bases_tinctures_and_unique_identities()
    {
        Saves.Open(Path.Combine(Path.GetTempPath(), "LocalExile-flasks-" + Guid.NewGuid().ToString("N") + ".db"));
        var catalog = new ItemCatalog("data");
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        string account = "FlaskFill" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        int tab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Flask);
        var life = catalog.All.Where(t => t.Class == "LifeFlask" && !t.Id.EndsWith("Royale")).Take(2).ToArray();
        var unique = ItemCatalog.Create(life[0]) with { Rarity = ItemRarity.Unique };
        var magic = ItemCatalog.Create(life[1]) with { Rarity = ItemRarity.Magic };
        Assert.Equal(2, stash.FillCells(tab, [unique, magic], i => i.TypeHash.ToString(), Shape, out _)!.Added.Count);
        var original = stash.Inventory(tab).Items.ToArray();
        var first = stash.FillFlasks(tab, catalog, Shape, out _)!;
        Assert.True(first.Added.Count > 60);
        Assert.Equal(1, first.Full);
        Assert.Equal(0, first.Skipped);
        var all = stash.Inventory(tab).Items;
        Assert.All(original, i => Assert.Contains(i, all));
        var ordinary = all.Where(i => i.Item.Rarity != ItemRarity.Unique && Shape(i.Item)!.Value.Class != "Tincture").ToArray();
        Assert.Equal(48, ordinary.Length);
        Assert.Equal(48, ordinary.Select(i => i.Item.TypeHash).Distinct().Count());
        Assert.All(ordinary, i => Assert.DoesNotContain("Royale", catalog.ByHash(i.Item.TypeHash)!.Id));
        Assert.Equal(12, ordinary.Count(i => Shape(i.Item)!.Value.Class == "LifeFlask"));
        Assert.Equal(12, ordinary.Count(i => Shape(i.Item)!.Value.Class == "ManaFlask"));
        Assert.Equal(6, ordinary.Count(i => Shape(i.Item)!.Value.Class == "HybridFlask"));
        Assert.Equal(18, ordinary.Count(i => Shape(i.Item)!.Value.Class == "UtilityFlask"));
        var tinctures = all.Where(i => Shape(i.Item)!.Value.Class == "Tincture").ToArray();
        Assert.Contains(tinctures, i => i.Item.Rarity == ItemRarity.Normal);
        Assert.Contains(tinctures, i => i.Item.Rarity == ItemRarity.Unique);
        var uniques = all.Where(i => i.Item.Rarity == ItemRarity.Unique && i.Item.NameWords.Count > 0).ToArray();
        Assert.True(uniques.Length > 20);
        Assert.Contains(uniques, i => Shape(i.Item)!.Value.Class == "UtilityFlask");
        var sharedBase = uniques.GroupBy(i => i.Item.TypeHash).First(g => g.Count() > 1);
        Assert.True(sharedBase.Select(i => string.Join(",", i.Item.NameWords)).Distinct().Count() > 1);
        var ids = all.Select(i => i.Id).Order().ToArray();
        AccountStash.Forget(account);
        stash = AccountStash.For(account, catalog);
        var again = stash.FillFlasks(tab, catalog, Shape, out _)!;
        Assert.Empty(again.Added);
        Assert.True(again.Full > 60);
        Assert.Equal(0, again.Skipped);
        Assert.Equal(ids, stash.Inventory(tab).Items.Select(i => i.Id).Order());
    }
}

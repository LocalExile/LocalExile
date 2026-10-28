using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Unique items whose client name/art come from Words/UniqueStashLayout and whose mods are curated (UniqueCatalog).
public class UniqueCatalogTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/Words.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("Words.json");
    }

    static readonly Lazy<(ItemCatalog Items, UniqueCatalog Uniques)> Setup = new(() =>
    {
        string data = Data();
        var items = new ItemCatalog(data);
        return (items, new UniqueCatalog(items, data));
    });

    [Fact]
    public void Mageblood_is_a_unique_heavy_belt_with_its_name_art_and_mods()
    {
        var (items, uniques) = Setup.Value;
        var item = uniques.Create("mageblood", new Random(1))!;

        Assert.Equal(ItemRarity.Unique, item.Rarity);
        Assert.True(item.Identified);
        Assert.Equal(items.Find("Metadata/Items/Belts/Belt4")!.Hash, item.TypeHash);
        Assert.Contains(3562871479u, item.NameWords);   // the Words HASH32 of "Mageblood"
        Assert.Equal(48209, item.VisualIdentity);       // UniqueBelt43 HASH16

        var mods = item.Mods.Select(m => (Mod: items.Mods.ByHash(m.Hash), Value: m)).Where(p => p.Mod is not null).ToList();
        Assert.Contains(mods, p => p.Mod!.Id == "StrengthImplicitBelt1" && p.Value.Section == ModSection.Implicit);
        foreach (string id in new[] { "DexterityUnique__3", "FireResistUnique__24", "ColdResistUnique__34",
                     "MagicUtilityFlasksCannotUseUnique__1____", "MagicUtilityFlasksCannotRemoveUnique__1" })
            Assert.Contains(mods, p => p.Mod!.Id == id && p.Value.Section == ModSection.Explicit);
        var always = item.Mods.Single(m => items.Mods.ByHash(m.Hash)?.Id == "MagicUtilityFlasksAlwaysApplyUnique__1");
        Assert.Equal(ModSection.Explicit, always.Section);
        Assert.InRange(always.Values[0], 2, 4);
    }

    [Fact]
    public void An_unknown_name_is_no_unique()
    {
        var (_, uniques) = Setup.Value;

        Assert.Null(uniques.Find("not a unique"));
        Assert.Null(uniques.Create("not a unique", new Random(2)));
    }
}

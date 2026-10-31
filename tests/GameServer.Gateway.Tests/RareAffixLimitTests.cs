using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The rare prefix/suffix caps in system_settings (ItemRolls.MaxAffixesPerSide): the defaults keep the game's
// 4-6 / 3-3 and jewel 3-4 / 2-2 distributions, while a raised cap lets the roll use the extra side.
public class RareAffixLimitTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/BaseItemTypes.json")))
                return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("BaseItemTypes.json");
    }

    static readonly Lazy<(ItemCatalog Items, string Data)> Setup = new(() =>
    {
        string data = Data();
        return (new ItemCatalog(data), data);
    });

    static ItemRolls Rolls(SystemSettings settings) => new(Setup.Value.Items, Setup.Value.Data, settings);

    static SystemSettings Caps(int prefix, int suffix, int jewelPrefix = 2, int jewelSuffix = 2)
    {
        var settings = new SystemSettings();
        settings.Replace(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["rare_max_prefixes"] = prefix,
            ["rare_max_suffixes"] = suffix,
            ["rare_jewel_max_prefixes"] = jewelPrefix,
            ["rare_jewel_max_suffixes"] = jewelSuffix,
        });
        return settings;
    }

    [Fact]
    public void The_default_caps_keep_the_existing_rare_counts()
    {
        Assert.Equal(4, ItemRolls.RareAffixCount(4, 3, 3, 6));
        Assert.Equal(5, ItemRolls.RareAffixCount(5, 3, 3, 6));
        Assert.Equal(6, ItemRolls.RareAffixCount(6, 3, 3, 6));
        Assert.Equal(3, ItemRolls.RareAffixCount(3, 2, 2, 4));
        Assert.Equal(4, ItemRolls.RareAffixCount(4, 2, 2, 4));
    }

    [Fact]
    public void A_raised_cap_allows_more_than_six_affixes_and_never_more_than_the_caps()
    {
        var rolls = Rolls(Caps(4, 4));
        var type = Setup.Value.Items.Find("Two-Stone Ring")!;
        var random = new Random(7);
        int overSix = 0;
        for (int i = 0; i < 300; i++)
        {
            var item = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 100 }, ItemRarity.Rare, random);
            var mods = item.Mods.Where(m => m.Section == ModSection.Explicit)
                .Select(m => Setup.Value.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Prefix), 0, 4);
            Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Suffix), 0, 4);
            Assert.InRange(mods.Length, 1, 8);
            if (mods.Length > 6) overSix++;
        }
        Assert.True(overSix > 0, "the raised caps should sometimes produce more than six affixes");
    }

    [Fact]
    public void A_lowered_cap_is_respected()
    {
        var rolls = Rolls(Caps(1, 1));
        var type = Setup.Value.Items.Find("Driftwood Wand")!;
        var random = new Random(11);
        for (int i = 0; i < 50; i++)
        {
            var item = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 84 }, ItemRarity.Rare, random);
            var mods = item.Mods.Where(m => m.Section == ModSection.Explicit)
                .Select(m => Setup.Value.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.InRange(mods.Length, 1, 2);
            Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Prefix), 0, 1);
            Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Suffix), 0, 1);
        }
    }

    [Fact]
    public void Jewels_use_their_own_default_caps()
    {
        // Changing the ordinary rare caps must not raise a jewel's 2/2 limit.
        var rolls = Rolls(Caps(4, 4));
        var type = Setup.Value.Items.Find("Crimson Jewel")!;
        var random = new Random(13);
        for (int i = 0; i < 100; i++)
        {
            var item = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 84 }, ItemRarity.Rare, random);
            var mods = item.Mods.Where(m => m.Section == ModSection.Explicit)
                .Select(m => Setup.Value.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.InRange(mods.Length, 3, 4);
            Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Prefix), 0, 2);
            Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Suffix), 0, 2);
        }
    }
}

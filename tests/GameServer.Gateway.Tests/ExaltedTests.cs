using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ExaltedTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;
    static readonly ItemCatalog.BaseType Jewel = Program.Items.Find("Metadata/Items/Jewels/JewelStr")!;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Rare(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item FourModWand() => Rare(Wand,
        Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
        Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")) with { ItemLevel = 1 };

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Exalted(item, Program.Rolls, new Random(seed));

    [Fact]
    public void A_full_rare_jewel_is_refused()
    {
        // CoE: jewel classes cap at 4 affixes (2 per side); a 2+2 jewel has no open modifier.
        var full = Program.Rolls.Roll(ItemCatalog.Create(Jewel) with { ItemLevel = 84 }, ItemRarity.Rare,
            new Random(5), shape: (4, 2));
        Assert.Equal(4, full.Mods.Count(m => m.Section == ModSection.Explicit));
        Assert.Null(Apply(full));
    }

    [Fact]
    public void An_unmodifiable_rare_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.True(type.Unmodifiable);
        var prefix = Program.Items.Mods.All.First(m => m.Domain == type.ModDomain &&
            m.GenerationType == ItemRolls.Prefix && m.Level <= 84);
        Assert.Null(Apply(Rare(type, prefix.Roll(ModSection.Explicit, new Random(1)))));
    }

    [Fact]
    public void A_rare_flask_is_refused()
    {
        var flask = Program.Items.Find("Small Life Flask")!;
        Assert.Null(Apply(Rare(flask, Explicit("FlaskIncreasedRecoverySpeed1"))));
    }

    [Fact]
    public void A_rare_wand_gets_exactly_one_more_affix_and_keeps_its_name_and_mods()
    {
        var before = FourModWand() with { Identified = false };
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.False(after.Identified);
        Assert.Equal(before.NameWords, after.NameWords);
        Assert.Equal(5, after.Mods.Count(m => m.Section == ModSection.Explicit));
        foreach (var mod in before.Mods.Where(m => m.Section == ModSection.Explicit))
            Assert.Contains(mod, after.Mods);
        var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit)
            .Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
        Assert.All(new[] { ItemRolls.Prefix, ItemRolls.Suffix },
            side => Assert.InRange(explicits.Count(m => m.GenerationType == side), 1, 3));
    }

    [Fact]
    public void The_pool_for_a_four_mod_rare_wand_matches_coe()
    {
        // CoE beta 3.29.3.1.2 observed these nine open modifiers, total weight 5150.
        var expected = new Dictionary<string, int>
        {
            ["LocalIncreasedPhysicalDamagePercentAndAccuracyRating1"] = 500,
            ["LocalAddedFireDamage1"] = 250,
            ["LifeGainedFromEnemyDeath1"] = 500,
            ["ManaGainedFromEnemyDeath1"] = 500,
            ["LocalCriticalStrikeChance1"] = 500,
            ["LocalIncreasedAccuracyNew1"] = 500,
            ["SpellAddedFireDamage1"] = 800,
            ["SpellAddedColdDamage1"] = 800,
            ["SpellAddedLightningDamage1"] = 800,
        };
        var actual = Program.Rolls.AffixesToAdd(FourModWand(), 3)
            .ToDictionary(p => p.Mod.Id, p => p.Weight);
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        Assert.Equal(5150, actual.Values.Sum());
    }

    [Fact]
    public void A_full_rare_wand_is_refused()
    {
        var full = Program.Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 84 }, ItemRarity.Rare,
            new Random(9), shape: (6, 3));
        Assert.Equal(6, full.Mods.Count(m => m.Section == ModSection.Explicit));
        Assert.Null(Apply(full));
    }

    [Fact]
    public void A_rare_jewel_with_two_prefixes_and_one_suffix_can_only_add_a_suffix()
    {
        Item? item = null;
        for (int seed = 0; seed < 200 && item is null; seed++)
        {
            var candidate = Program.Rolls.Roll(ItemCatalog.Create(Jewel) with { ItemLevel = 84 }, ItemRarity.Rare,
                new Random(seed), shape: (3, 2));
            var sides = candidate.Mods.Where(m => m.Section == ModSection.Explicit)
                .Select(m => Program.Items.Mods.ByHash(m.Hash)!.GenerationType).ToArray();
            if (sides.Count(s => s == ItemRolls.Prefix) == 2 && sides.Count(s => s == ItemRolls.Suffix) == 1)
                item = candidate;
        }
        Assert.NotNull(item);
        Assert.All(Program.Rolls.AffixesToAdd(item, 2),
            p => Assert.Equal(ItemRolls.Suffix, p.Mod.GenerationType));
        var after = Apply(item!)!;
        Assert.Equal(4, after.Mods.Count(m => m.Section == ModSection.Explicit));
        var added = Program.Items.Mods.ByHash(after.Mods.Last(m => m.Section == ModSection.Explicit).Hash)!;
        Assert.Equal(ItemRolls.Suffix, added.GenerationType);
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Unique)]
    public void Non_rare_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(FourModWand() with { Rarity = rarity }));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Corrupted_and_mirrored_rare_items_are_refused(bool corrupted, bool mirrored) =>
        Assert.Null(Apply(FourModWand() with { Corrupted = corrupted, Mirrored = mirrored }));

    [Fact]
    public void Unknown_unsupported_no_Mods_and_empty_pool_bases_are_refused()
    {
        Assert.False(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!.Supported);
        Assert.Null(Apply(Rare(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!)));
        Assert.Null(Apply(Rare(Program.Items.Find("Metadata/Items/Currency/CurrencyAddModToRare")!)));
        Assert.Null(Apply(FourModWand() with { TypeHash = 0xdeadbeef }));
        Assert.Null(Apply(FourModWand() with { ItemLevel = 0 }));
    }

    [Fact]
    public void Exalted_preserves_non_explicit_mods_and_other_properties()
    {
        var before = FourModWand() with
        {
            Quality = 17,
            Charges = 23,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            Mods = [new(ModSection.Implicit, 108, [15]), new(ModSection.Enchant, 108, [19]),
                new(ModSection.Explicit, Mod("LocalIncreasedPhysicalDamagePercent1").Hash, [45]),
                new(ModSection.Explicit, Mod("IncreasedManaWeapon1").Hash, [35]),
                new(ModSection.Explicit, Mod("LocalIncreasedAttackSpeed1").Hash, [6]),
                new(ModSection.Explicit, Mod("Intelligence1").Hash, [10])],
        };
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Equal(before.NameWords, after.NameWords);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.Charges, after.Charges);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.Equal(5, after.Mods.Count(m => m.Section == ModSection.Explicit));
    }
}

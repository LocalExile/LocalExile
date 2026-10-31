using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AugmentationTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Magic(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Magic,
            Mods = mods,
        };

    static Item Magic(params ItemMod[] mods) => Magic(Wand, mods);

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Augmentation(item, Program.Rolls, new Random(seed));

    static Dictionary<string, int> Pool(Item item) =>
        Program.Rolls.AffixesToAdd(item, 1).ToDictionary(p => p.Mod.Id, p => p.Weight);

    [Fact]
    public void An_unmodifiable_magic_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.True(type.Unmodifiable);
        var prefix = Program.Items.Mods.All.First(m => m.Domain == type.ModDomain &&
            m.GenerationType == ItemRolls.Prefix && m.Level <= 84);
        var item = Magic(type, prefix.Roll(ModSection.Explicit, new Random(1)));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void An_unsupported_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!;
        Assert.False(type.Supported);
        Assert.Null(Apply(Magic(type)));
    }

    [Fact]
    public void A_base_without_mods_and_an_unknown_base_are_refused()
    {
        Assert.Null(Apply(Magic(Program.Items.Find("Metadata/Items/Currency/CurrencyUpgradeToMagic")!)));
        Assert.Null(Apply(Magic() with { TypeHash = 0xdeadbeef }));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Corrupted_and_mirrored_magic_items_are_refused(bool corrupted, bool mirrored)
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")) with
        {
            Corrupted = corrupted,
            Mirrored = mirrored,
        };
        Assert.Null(Apply(item));
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void Non_magic_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Rarity = rarity }));

    [Fact]
    public void A_magic_item_with_a_prefix_has_exactly_the_six_open_suffixes()
    {
        // CoE beta 3.29.3.1.2 pool for a magic Driftwood Wand (ilvl 1) with
        // LocalIncreasedPhysicalDamagePercent1; provenance is in Agents/docs/re/augmentation-coe.md.
        var expected = new Dictionary<string, int>
        {
            ["Intelligence1"] = 333, ["LocalIncreasedAttackSpeed1"] = 500,
            ["LifeGainedFromEnemyDeath1"] = 500, ["ManaGainedFromEnemyDeath1"] = 500,
            ["LocalCriticalStrikeChance1"] = 500, ["LocalIncreasedAccuracyNew1"] = 500,
        };
        var actual = Pool(Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")));
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        Assert.Equal(2833, actual.Values.Sum());
        Assert.All(actual.Keys, id => Assert.Equal(ItemRolls.Suffix, Mod(id).GenerationType));
    }

    [Fact]
    public void A_magic_item_with_a_suffix_has_exactly_the_seven_open_prefixes()
    {
        var expected = new Dictionary<string, int>
        {
            ["IncreasedManaWeapon1"] = 1000,
            ["LocalIncreasedPhysicalDamagePercentAndAccuracyRating1"] = 500,
            ["LocalIncreasedPhysicalDamagePercent1"] = 500,
            ["LocalAddedFireDamage1"] = 250,
            ["SpellAddedFireDamage1"] = 800,
            ["SpellAddedColdDamage1"] = 800,
            ["SpellAddedLightningDamage1"] = 800,
        };
        var actual = Pool(Magic(Explicit("LocalIncreasedAttackSpeed1")));
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        Assert.Equal(4650, actual.Values.Sum());
        Assert.All(actual.Keys, id => Assert.Equal(ItemRolls.Prefix, Mod(id).GenerationType));
    }

    [Fact]
    public void The_added_affix_uses_the_coe_weights()
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"));
        var random = new Random(123);
        const int trials = 20000;
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < trials; i++)
        {
            var added = Program.Items.Mods.ByHash(
                CurrencyEffects.Augmentation(item, Program.Rolls, random)!
                    .Mods.Last(m => m.Section == ModSection.Explicit).Hash)!;
            counts[added.Id] = counts.GetValueOrDefault(added.Id) + 1;
        }
        Assert.Equal(new[]
        {
            "Intelligence1", "LocalIncreasedAttackSpeed1", "LifeGainedFromEnemyDeath1",
            "ManaGainedFromEnemyDeath1", "LocalCriticalStrikeChance1", "LocalIncreasedAccuracyNew1",
        }.Order(), counts.Keys.Order());
        Assert.InRange((double)counts["Intelligence1"] / trials, 0.107, 0.128);
        foreach (string id in counts.Keys.Where(id => id != "Intelligence1"))
            Assert.InRange((double)counts[id] / trials, 0.166, 0.187);
    }

    [Fact]
    public void A_full_magic_item_has_no_affix_to_add()
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1"));
        Assert.Empty(Pool(item));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void A_magic_flask_with_a_prefix_has_no_affix_to_add()
    {
        // Small Life Flask has two level-1 prefixes and no suffixes, so a magic one is already full.
        var flask = Program.Items.Find("Small Life Flask")!;
        var item = Magic(flask, Explicit("FlaskIncreasedRecoverySpeed1"));
        Assert.Empty(Pool(item));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void An_empty_magic_item_can_receive_one_affix_from_the_full_weighted_pool()
    {
        var item = Magic() with { Identified = false };
        Assert.Equal(7483, Pool(item).Values.Sum());
        var after = Apply(item)!;
        Assert.Equal(ItemRarity.Magic, after.Rarity);
        Assert.False(after.Identified);
        Assert.Single(after.Mods, m => m.Section == ModSection.Explicit);
    }

    [Fact]
    public void The_added_affix_is_on_the_open_side_and_the_existing_affixes_stay()
    {
        var prefix = Explicit("LocalIncreasedPhysicalDamagePercent1");
        var item = Magic(prefix);
        var after = Apply(item)!;
        var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
        Assert.Equal(2, explicits.Length);
        Assert.Equal(prefix, explicits[0]);
        var added = Program.Items.Mods.ByHash(explicits[1].Hash)!;
        Assert.Equal(ItemRolls.Suffix, added.GenerationType);
        for (int stat = 0; stat < added.Stats.Count; stat++)
            Assert.InRange(explicits[1].Values[stat], Math.Min(added.Stats[stat].Min, added.Stats[stat].Max),
                Math.Max(added.Stats[stat].Min, added.Stats[stat].Max));
    }

    [Fact]
    public void Adding_an_affix_preserves_other_properties_and_an_unidentified_item_stays_unidentified()
    {
        var before = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")) with
        {
            Quality = 17,
            Charges = 23,
            Identified = false,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            Mods = [new(ModSection.Implicit, 108, [15]), new(ModSection.Enchant, 108, [19]),
                new(ModSection.Explicit, Mod("LocalIncreasedPhysicalDamagePercent1").Hash, [45])],
        };
        var after = Apply(before)!;
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.Charges, after.Charges);
        Assert.False(after.Identified);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.Equal(Mod("LocalIncreasedPhysicalDamagePercent1").Hash, after.Mods.First(m => m.Section == ModSection.Explicit).Hash);
    }
}

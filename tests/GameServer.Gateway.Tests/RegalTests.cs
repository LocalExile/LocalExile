using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RegalTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Magic(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Magic,
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item Magic(params ItemMod[] mods) => Magic(Wand, mods);

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Regal(item, Program.Rolls, new Random(seed));

    static Dictionary<string, int> Pool(Item item) =>
        Program.Rolls.AffixesToAdd(item, 3).ToDictionary(p => p.Mod.Id, p => p.Weight);

    [Fact]
    public void A_magic_flask_is_refused_because_flasks_are_magic_only()
    {
        // CoE's class data calls flasks magic-only; the official game has no rare flasks.
        var flask = Program.Items.Find("Small Life Flask")!;
        var item = Magic(flask, Explicit("FlaskIncreasedRecoverySpeed1"));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void An_unmodifiable_magic_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.True(type.Unmodifiable);
        var prefix = Program.Items.Mods.All.First(m => m.Domain == type.ModDomain &&
            m.GenerationType == ItemRolls.Prefix && m.Level <= 84);
        Assert.Null(Apply(Magic(type, prefix.Roll(ModSection.Explicit, new Random(1)))));
    }

    [Fact]
    public void An_unsupported_base_and_a_base_without_mods_are_refused()
    {
        Assert.False(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!.Supported);
        Assert.Null(Apply(Magic(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!)));
        Assert.Null(Apply(Magic(Program.Items.Find("Metadata/Items/Currency/CurrencyUpgradeToRare")!)));
    }

    [Fact]
    public void An_unknown_base_is_refused() =>
        Assert.Null(Apply(Magic() with { TypeHash = 0xdeadbeef }));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Corrupted_and_mirrored_magic_items_are_refused(bool corrupted, bool mirrored)
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Corrupted = corrupted, Mirrored = mirrored };
        Assert.Null(Apply(item));
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void Non_magic_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Rarity = rarity }));

    [Fact]
    public void A_magic_wand_becomes_rare_with_one_more_affix_and_a_rare_name()
    {
        var prefix = Explicit("LocalIncreasedPhysicalDamagePercent1");
        var before = Magic(prefix);
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Identified);
        Assert.Equal(2, after.NameWords.Count);
        var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
        Assert.Equal(2, explicits.Length);
        Assert.Contains(prefix, explicits);
        var added = Program.Items.Mods.ByHash(explicits.Single(m => m.Hash != prefix.Hash).Hash)!;
        Assert.NotEqual("LocalIncreasedPhysicalDamagePercent1", added.Id);
    }

    [Fact]
    public void A_full_magic_item_becomes_rare_with_three_affixes()
    {
        // CoE apply on a magic item with both sides full adds a third affix; its pool query is misleading.
        var before = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1"));
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Equal(3, after.Mods.Count(m => m.Section == ModSection.Explicit));
        Assert.Equal(2, after.NameWords.Count);
    }

    [Theory]
    [InlineData("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")]
    [InlineData("Metadata/Items/Armours/BodyArmours/BodyInt17")]
    [InlineData("Metadata/Items/Rings/Ring2")]
    [InlineData("Metadata/Items/Jewels/JewelStr")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    public void Native_magic_bases_become_rare_with_exactly_one_more_affix(string id)
    {
        var type = Program.Items.Find(id)!;
        for (int seed = 0; seed < 20; seed++)
        {
            var magic = Program.Rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 84 }, ItemRarity.Magic, new Random(seed));
            int before = magic.Mods.Count(m => m.Section == ModSection.Explicit);
            var after = Apply(magic, seed);
            Assert.NotNull(after);
            Assert.Equal(ItemRarity.Rare, after.Rarity);
            Assert.Equal(before + 1, after.Mods.Count(m => m.Section == ModSection.Explicit));
            Assert.Equal(2, after.NameWords.Count);
        }
    }

    [Fact]
    public void The_rare_pool_keeps_the_magic_mod_and_opens_both_sides()
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"));
        var pool = Program.Rolls.AffixesToAdd(item, 3);
        Assert.Equal(12, pool.Count);
        Assert.Equal(6983, pool.Sum(p => p.Weight));
        Assert.Equal(6, pool.Count(p => p.Mod.GenerationType == ItemRolls.Prefix));
        Assert.Equal(6, pool.Count(p => p.Mod.GenerationType == ItemRolls.Suffix));
        Assert.DoesNotContain(pool, p => p.Mod.Id == "LocalIncreasedPhysicalDamagePercent1");
    }

    [Fact]
    public void A_full_magic_item_has_the_remaining_eleven_candidates()
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1"));
        var pool = Program.Rolls.AffixesToAdd(item, 3);
        Assert.Equal(11, pool.Count);
        Assert.Equal(6483, pool.Sum(p => p.Weight));
    }

    [Fact]
    public void The_added_affix_uses_the_coe_rare_pool_weights()
    {
        // CoE apply on 300 seeds with this prefix produced all 12 candidates and 172 prefixes / 128 suffixes.
        var before = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"));
        var random = new Random(123);
        const int trials = 20000;
        int prefixes = 0;
        var seen = new HashSet<string>();
        for (int i = 0; i < trials; i++)
        {
            var after = CurrencyEffects.Regal(before, Program.Rolls, random)!;
            var added = Program.Items.Mods.ByHash(after.Mods.Last(m => m.Section == ModSection.Explicit).Hash)!;
            seen.Add(added.Id);
            if (added.GenerationType == ItemRolls.Prefix) prefixes++;
            Assert.NotEqual("LocalIncreasedPhysicalDamagePercent1", added.Id);
        }
        Assert.Equal(12, seen.Count);
        Assert.InRange((double)prefixes / trials, 0.582, 0.606); // 4150 / 6983 = 0.5943
    }

    [Fact]
    public void Regal_preserves_other_properties_and_sets_the_identified_rare()
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
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Identified);
        Assert.Equal(2, after.NameWords.Count);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.Charges, after.Charges);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.Equal(Mod("LocalIncreasedPhysicalDamagePercent1").Hash, after.Mods.First(m => m.Section == ModSection.Explicit).Hash);
    }
}

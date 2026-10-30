using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AlchemyTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

    static Item Normal(ItemCatalog.BaseType type) => ItemCatalog.Create(type);

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Alchemy(item, Program.Rolls, new Random(seed));

    [Fact]
    public void A_normal_wand_becomes_rare_with_a_name_and_weighted_pool_mods()
    {
        var after = Apply(Normal(Wand) with { ItemLevel = 1 })!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Identified);
        Assert.Equal(2, after.NameWords.Count);
        var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
        Assert.InRange(explicits.Length, 4, 6);
        var mods = explicits.Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
        Assert.All(new[] { ItemRolls.Prefix, ItemRolls.Suffix },
            side => Assert.InRange(mods.Count(m => m.GenerationType == side), 1, 3));
        var families = mods.SelectMany(m => m.Families).ToArray();
        Assert.Equal(families.Length, families.Distinct().Count());
        foreach (var (mod, rolled) in mods.Zip(explicits))
        {
            Assert.Equal(Wand.ModDomain, mod.Domain);
            Assert.True(mod.WeightFor(Wand.Tags) > 0);
            for (int stat = 0; stat < mod.Stats.Count; stat++)
                Assert.InRange(rolled.Values[stat], Math.Min(mod.Stats[stat].Min, mod.Stats[stat].Max),
                    Math.Max(mod.Stats[stat].Min, mod.Stats[stat].Max));
        }
    }

    [Theory]
    [InlineData("Metadata/Items/Rings/Ring2")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    [InlineData("Metadata/Items/Armours/BodyArmours/BodyInt17")]
    public void Native_normal_bases_become_rare_with_four_to_six_affixes(string id)
    {
        var type = Program.Items.Find(id)!;
        for (int seed = 0; seed < 20; seed++)
        {
            var after = Apply(Normal(type) with { ItemLevel = 84 }, seed)!;
            Assert.Equal(ItemRarity.Rare, after.Rarity);
            Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
            Assert.Equal(2, after.NameWords.Count);
        }
    }

    [Fact]
    public void A_normal_flask_is_refused_because_flasks_are_magic_only()
    {
        var flask = Program.Items.Find("Small Life Flask")!;
        Assert.Null(Apply(Normal(flask)));
    }

    [Fact]
    public void An_unmodifiable_normal_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.True(type.Unmodifiable);
        Assert.Null(Apply(Normal(type)));
    }

    [Theory]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void Non_normal_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(Normal(Wand) with { Rarity = rarity }));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Corrupted_and_mirrored_normal_items_are_refused(bool corrupted, bool mirrored) =>
        Assert.Null(Apply(Normal(Wand) with { Corrupted = corrupted, Mirrored = mirrored }));

    [Fact]
    public void Unknown_unsupported_and_no_Mods_bases_are_refused()
    {
        Assert.False(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!.Supported);
        Assert.Null(Apply(Normal(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!)));
        Assert.Null(Apply(Normal(Program.Items.Find("Metadata/Items/Currency/CurrencyUpgradeToRare")!)));
        Assert.Null(Apply(Normal(Wand) with { TypeHash = 0xdeadbeef }));
    }

    [Fact]
    public void Alchemy_preserves_non_explicit_mods_and_other_properties()
    {
        var before = Normal(Wand) with
        {
            ItemLevel = 84,
            Quality = 17,
            Charges = 23,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            Mods = [new(ModSection.Implicit, 108, [15]), new(ModSection.Enchant, 108, [19])],
        };
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Identified);
        Assert.Equal(2, after.NameWords.Count);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.Charges, after.Charges);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods, after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
    }

    [Fact]
    public void An_empty_pool_is_refused_instead_of_spending_an_orb()
    {
        var item = Normal(Wand) with { ItemLevel = 0 };
        Assert.Empty(Program.Rolls.Pool(Wand, 0));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void A_normal_jewel_gets_three_or_four_mods_with_at_most_two_per_side()
    {
        // CoE beta: jewel classes have affixes=4 and affixNumberGeneration[4] = [.65,.35].
        var jewel = Program.Items.Find("Metadata/Items/Jewels/JewelStr")!;
        var random = new Random(77);
        int three = 0;
        for (int i = 0; i < 500; i++)
        {
            var after = CurrencyEffects.Alchemy(ItemCatalog.Create(jewel) with { ItemLevel = 84 },
                Program.Rolls, random)!;
            var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
            Assert.InRange(explicits.Length, 3, 4);
            var mods = explicits.Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.All(new[] { ItemRolls.Prefix, ItemRolls.Suffix },
                side => Assert.InRange(mods.Count(m => m.GenerationType == side), 1, 2));
            if (explicits.Length == 3) three++;
        }
        Assert.InRange((double)three / 500, 0.58, 0.72);
    }

    [Fact]
    public void The_rare_count_distribution_matches_coe_affix_number_generation()
    {
        // CoE beta 3.29.3.1.2 affixNumberGeneration[6] = [2/3, 1/4, 1/12] for 4, 5 and 6 affixes.
        var random = new Random(321);
        const int trials = 20000;
        int four = 0, five = 0, six = 0;
        for (int i = 0; i < trials; i++)
        {
            var after = CurrencyEffects.Alchemy(Normal(Wand), Program.Rolls, random)!;
            Assert.Equal(ItemRarity.Rare, after.Rarity);
            Assert.True(after.Identified);
            int count = after.Mods.Count(m => m.Section == ModSection.Explicit);
            if (count == 4) four++;
            else if (count == 5) five++;
            else if (count == 6) six++;
            else Assert.Fail($"unexpected affix count {count}");
        }
        Assert.InRange((double)four / trials, 0.655, 0.678);
        Assert.InRange((double)five / trials, 0.238, 0.262);
        Assert.InRange((double)six / trials, 0.078, 0.088);
    }
}

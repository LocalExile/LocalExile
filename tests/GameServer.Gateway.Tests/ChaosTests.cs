using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChaosTests
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

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Chaos(item, Program.Rolls, new Random(seed));

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
    public void An_empty_pool_is_refused_instead_of_stripping_the_item()
    {
        var item = FourModWand() with { ItemLevel = 0 };
        Assert.Empty(Program.Rolls.Pool(Wand, 0));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void A_rare_wand_is_rerolled_with_a_new_name_and_four_to_six_affixes()
    {
        var before = FourModWand() with { Quality = 17 };
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Identified);
        Assert.Equal(2, after.NameWords.Count);
        Assert.NotEqual(before.NameWords, after.NameWords);
        Assert.Equal(17, after.Quality);
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
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
    }

    [Fact]
    public void The_rare_count_distribution_matches_coe_affix_number_generation()
    {
        // CoE beta affixNumberGeneration[6] = [2/3, 1/4, 1/12]; 300 applies gave 211/63/26.
        var random = new Random(321);
        const int trials = 5000;
        int four = 0, five = 0, six = 0;
        for (int i = 0; i < trials; i++)
        {
            var after = CurrencyEffects.Chaos(FourModWand(), Program.Rolls, random)!;
            int count = after.Mods.Count(m => m.Section == ModSection.Explicit);
            if (count == 4) four++;
            else if (count == 5) five++;
            else if (count == 6) six++;
            else Assert.Fail($"unexpected affix count {count}");
        }
        Assert.InRange((double)four / trials, 0.645, 0.688);
        Assert.InRange((double)five / trials, 0.225, 0.275);
        Assert.InRange((double)six / trials, 0.068, 0.098);
    }

    [Fact]
    public void A_full_rare_item_is_rerolled_not_refused()
    {
        var full = Program.Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 84 }, ItemRarity.Rare,
            new Random(9), shape: (6, 3));
        Assert.Equal(6, full.Mods.Count(m => m.Section == ModSection.Explicit));
        var after = Apply(full)!;
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
    }

    [Fact]
    public void A_rare_jewel_rerolls_three_or_four_affixes_with_two_per_side()
    {
        var random = new Random(77);
        for (int i = 0; i < 100; i++)
        {
            var jewel = Program.Rolls.Roll(ItemCatalog.Create(Jewel) with { ItemLevel = 84 }, ItemRarity.Rare,
                random, shape: (4, 2));
            var after = CurrencyEffects.Chaos(jewel, Program.Rolls, random)!;
            var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
            Assert.InRange(explicits.Length, 3, 4);
            var mods = explicits.Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.All(new[] { ItemRolls.Prefix, ItemRolls.Suffix },
                side => Assert.InRange(mods.Count(m => m.GenerationType == side), 1, 2));
        }
    }

    [Fact]
    public void A_prefix_lock_keeps_the_prefixes_and_the_craft_is_consumed()
    {
        var craft = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == "item_generation_cannot_change_prefixes"));
        var prefix = Explicit("LocalIncreasedPhysicalDamagePercent1");
        var item = Rare(Wand, prefix, Explicit("LocalIncreasedAttackSpeed1")) with
        {
            Mods = [.. Rare(Wand, prefix, Explicit("LocalIncreasedAttackSpeed1")).Mods,
                craft.Roll(ModSection.Explicit, new Random(3))],
        };
        var after = Apply(item)!;
        Assert.Contains(prefix, after.Mods);
        Assert.DoesNotContain(after.Mods, m => m.Hash == craft.Hash);
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
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
    public void Unknown_unsupported_and_no_Mods_bases_are_refused()
    {
        Assert.False(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!.Supported);
        Assert.Null(Apply(Rare(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintControlBlocks")!)));
        Assert.Null(Apply(Rare(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!)));
        Assert.Null(Apply(FourModWand() with { TypeHash = 0xdeadbeef }));
    }
}

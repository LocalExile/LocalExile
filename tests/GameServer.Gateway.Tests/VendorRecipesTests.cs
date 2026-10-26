using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public class VendorRecipesTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static readonly Lazy<VendorRecipes> Sales = new(() => new(Catalog, BenchCraftingTests.Data()));
    static Item Make(string name, ItemRarity rarity = ItemRarity.Normal, int level = 70, bool identified = true, int quality = 0, int count = 1)
    {
        var type = Catalog.Find(name)!;
        return ItemCatalog.Create(type) with { Rarity = rarity, ItemLevel = (byte)level, Identified = identified, Quality = (byte)quality, StackSize = (ushort)count };
    }
    static VendorRecipes.Quote Quote(params Item[] items) => Sales.Value.Evaluate(items.Select((i, n) => new InventoryItem((uint)n + 1, 0, 0, i)), new Random(27));
    static int Count(VendorRecipes.Quote quote, string name) => quote.Rewards.Where(i => Catalog.ByHash(i.TypeHash)!.Name == name).Sum(i => i.StackSize);
    static Item[] RareSet(int level, bool identified, int quality, string weapon = "Crude Bow") =>
        new[] { "Iron Hat", "Plate Vest", "Iron Gauntlets", "Iron Greaves", "Rustic Sash", "Amber Amulet", "Iron Ring", "Paua Ring", weapon }
            .Select(n => Make(n, ItemRarity.Rare, level, identified, quality)).ToArray();

    [Theory]
    [InlineData(59, "Orb of Chance")] [InlineData(60, "Chaos Orb")]
    [InlineData(74, "Chaos Orb")] [InlineData(75, "Regal Orb")] [InlineData(100, "Regal Orb")]
    public void Full_set_uses_lowest_item_level_and_accepts_two_handed_weapons(int level, string reward)
    {
        var set = RareSet(100, true, 0);
        set[0] = set[0] with { ItemLevel = (byte)level };
        Assert.Equal(1, Count(Quote(set), reward));
    }

    [Theory]
    [InlineData(true, 0, 1)] [InlineData(false, 0, 2)] [InlineData(true, 20, 2)] [InlineData(false, 20, 3)]
    public void Rare_set_bonuses_are_independent_and_jewellery_does_not_need_catalysts(bool identified, int quality, int count)
    {
        var set = RareSet(70, identified, quality);
        for (int i = 4; i < 8; i++) set[i] = set[i] with { Quality = 0 };
        var quote = Quote(set);
        Assert.Equal(count, Count(quote, "Chaos Orb"));
        Assert.Single(quote.Rewards);
    }

    [Theory]
    [InlineData("Rusted Sword", "Splintered Tower Shield")]
    [InlineData("Rusted Sword", "Driftwood Wand")]
    public void Rare_sets_accept_one_hand_and_offhand_but_never_quivers(string first, string second)
    {
        var set = RareSet(70, false, 0, first).ToList();
        set.Add(Make(second, ItemRarity.Rare, identified: false));
        Assert.Equal(2, Count(Quote(set.ToArray()), "Chaos Orb"));
        set[^1] = Make("Serrated Arrow Quiver", ItemRarity.Rare, identified: false);
        Assert.Equal(0, Count(Quote(set.ToArray()), "Chaos Orb"));
    }

    [Fact]
    public void Two_sets_and_unrelated_items_each_contribute_only_once()
    {
        var quote = Quote([.. RareSet(70, false, 0), .. RareSet(70, false, 0), Make("Portal Scroll", count: 3)]);
        Assert.Equal(4, Count(quote, "Chaos Orb"));
        Assert.Equal(3, Count(quote, "Scroll of Wisdom"));
    }

    [Fact]
    public void Gem_recipe_returns_unused_scouring_units_instead_of_selling_them_as_fragments()
    {
        var gem = Make("Fireball");
        gem = gem with { GemExperience = Catalog.Gem(Catalog.ByHash(gem.TypeHash)!)!.ExperienceFor(10) };
        var quote = Quote(gem, Make("Orb of Scouring", count: 8));
        Assert.Equal(7, Count(quote, "Orb of Scouring"));
        Assert.Equal(2, quote.Rewards.Count);
        Assert.Equal(9, Catalog.GemLevel(quote.Rewards.Single(i => i.TypeHash == gem.TypeHash)));
    }

    [Fact]
    public void Double_counting_an_input_id_is_rejected()
    {
        var item = new InventoryItem(1, 0, 0, Make("Orb of Transmutation"));
        Assert.Throws<ArgumentException>(() => Sales.Value.Evaluate([item, item]));
    }

    [Theory]
    [InlineData("Fireball")] [InlineData("Added Cold Damage Support")]
    public void Scouring_lowers_a_gem_one_level_preserving_quality_and_returning_surplus(string name)
    {
        var gem = Make(name, quality: 17);
        var info = Catalog.Gem(Catalog.ByHash(gem.TypeHash)!)!;
        gem = gem with { GemExperience = info.ExperienceFor(10) + 15 };
        var quote = Quote(gem, Make("Orb of Scouring", count: 1));
        var output = Assert.Single(quote.Rewards);
        Assert.Equal(gem.TypeHash, output.TypeHash);
        Assert.Equal(9, Catalog.GemLevel(output));
        Assert.Equal(17, output.Quality);
        Assert.Equal(info.ExperienceFor(9), output.GemExperience);
    }

    [Fact]
    public void Regret_resets_to_one_and_corrupted_gems_cannot_be_downgraded()
    {
        var gem = Make("Fireball");
        gem = gem with { GemExperience = Catalog.Gem(Catalog.ByHash(gem.TypeHash)!)!.ExperienceFor(15) };
        var output = Assert.Single(Quote(gem, Make("Orb of Regret")).Rewards);
        Assert.Equal(1, Catalog.GemLevel(output));
        Assert.DoesNotContain(Quote(gem with { Corrupted = true }, Make("Orb of Scouring")).Rewards, i => i.TypeHash == gem.TypeHash);
    }

    [Theory]
    [InlineData("Fireball", false)] [InlineData("Added Cold Damage Support", true)] [InlineData("Awakened Added Cold Damage Support", false)]
    public void Quality_reset_recipe_is_only_for_non_awakened_support_gems(string name, bool matches)
    {
        var gem = Make(name);
        gem = gem with { GemExperience = Catalog.Gem(Catalog.ByHash(gem.TypeHash)!)!.ExperienceFor(20) };
        var quote = Quote(gem, Make("Gemcutter's Prism"));
        Assert.Equal(matches, quote.Rewards.Any(i => i.TypeHash == gem.TypeHash && Catalog.GemLevel(i) == 1 && i.Quality == 20));
    }

    [Theory]
    [InlineData(6, 6, "Orb of Fusing", 20)]
    [InlineData(6, 0, "Jeweller's Orb", 7)]
    [InlineData(3, 3, "Chromatic Orb", 1)]
    public void Socket_rewards_use_link_groups_not_just_colours(int sockets, int linked, string name, int count)
    {
        var item = Make("Plate Vest") with { Sockets = Enumerable.Range(0, sockets).Select(i => new ItemSocket((SocketColour)(i % 3 + 1))).ToArray(),
            Links = linked > 0 ? [(byte)linked] : null };
        Assert.Equal(count, Count(Quote(item), name));
        if (sockets == 3) Assert.Equal(0, Count(Quote(item with { Links = [1, 1, 1] }), "Chromatic Orb"));
    }

    [Fact]
    public void Six_link_takes_precedence_over_a_full_rare_set()
    {
        var set = RareSet(70, false, 0);
        set[^1] = set[^1] with { Sockets = Enumerable.Repeat(new ItemSocket(SocketColour.White), 6).ToArray(), Links = [6] };
        var quote = Quote(set);
        Assert.Equal(20, Count(quote, "Orb of Fusing"));
        Assert.Equal(0, Count(quote, "Chaos Orb"));
    }

    [Fact]
    public void Quality_subsets_cannot_pool_unusable_remainders_and_twenty_quality_singles_pay_individually()
    {
        var items = new[] { 19, 13, 15, 9, 17, 7 }.Select(q => Make("Fireball", quality: q)).ToArray();
        Assert.Equal(1, Count(Quote(items), "Gemcutter's Prism"));
        Assert.Equal(2, Count(Quote(Make("Fireball", quality: 20), Make("Ice Nova", quality: 20)), "Gemcutter's Prism"));
        Assert.Equal(1, Count(Quote(Make("Plate Vest", quality: 20)), "Armourer's Scrap"));
    }

    [Fact]
    public void Downgrade_currency_is_counted_per_unit_and_fragments_combine_in_the_offer()
    {
        Assert.Equal(12, Count(Quote(Make("Orb of Transmutation", count: 3)), "Scroll of Wisdom"));
        Assert.Equal(1, Count(Quote(Enumerable.Range(0, 5).Select(_ => Make("Iron Hat")).ToArray()), "Scroll of Wisdom"));
        Assert.Equal(2, Count(Quote(Make("Iron Hat", ItemRarity.Magic, identified: false)), "Transmutation Shard"));
        Assert.Equal(5, Count(Quote(Make("Iron Hat", ItemRarity.Rare, identified: false)), "Transmutation Shard"));
    }

    [Theory]
    [InlineData("Whispering Essence of Hatred", "Muttering Essence of Hatred")]
    [InlineData("Clear Oil", "Sepia Oil")]
    public void Currency_upgrade_consumes_exactly_three_and_returns_surplus(string from, string to)
    {
        var quote = Quote(Make(from, count: 7));
        Assert.Equal(2, Count(quote, to));
        Assert.Equal(1, Count(quote, from));
        Assert.Equal(2, quote.Rewards.Count);
    }

    [Fact]
    public void Two_matching_rare_names_and_five_matching_bases_are_different_recipes()
    {
        var first = Make("Iron Ring", ItemRarity.Rare) with { NameWords = [12, 34] };
        Assert.Equal(1, Count(Quote(first, first), "Orb of Chance"));
        var five = Enumerable.Range(0, 5).Select(_ => Make("Iron Ring", ItemRarity.Rare)).ToArray();
        var output = Assert.Single(Quote(five).Rewards);
        Assert.Equal(five[0].TypeHash, output.TypeHash);
        Assert.Equal(ItemRarity.Rare, output.Rarity);
        Assert.False(output.Identified);
    }

    [Theory]
    [InlineData("Strength1", false, 2, 0)]
    [InlineData("Strength1", true, 8, 0)]
    [InlineData("IncreasedLife2", false, 4, 0)]
    public void Identified_affix_prices_follow_their_native_mod_family_and_fracture_multiplier(string modId, bool fractured, int alteration, int alchemy)
    {
        var mod = Catalog.Mods.ById(modId)!;
        var item = Make("Iron Ring", ItemRarity.Magic) with { Mods = [mod.Roll(ModSection.Explicit, new Random(2))], FracturedCount = fractured ? (byte)1 : (byte)0 };
        Assert.Equal((alteration, alchemy), Sales.Value.ModPrice(item));
    }

    [Theory]
    [InlineData(2, "Orb of Scouring")]
    [InlineData(6, "Orb of Augmentation")]
    public void Explicit_affixes_are_counted_as_modifiers_not_as_displayed_stat_lines(int count, string name)
    {
        var mod = Catalog.Mods.ById("LocalIncreasedPhysicalDamagePercentAndAccuracyRating1")!;
        var item = Make("Rusted Sword", ItemRarity.Rare) with { Mods = Enumerable.Range(0, count).Select(_ => mod.Roll(ModSection.Explicit, new Random(1))).ToArray() };
        Assert.Equal(1, Count(Quote(item), name));
    }

    [Fact]
    public void Flask_upgrades_and_hybrid_recipe_work_without_using_their_ordinary_sale_value()
    {
        Assert.Equal(1, Count(Quote(Make("Small Life Flask"), Make("Small Life Flask"), Make("Small Life Flask")), "Medium Life Flask"));
        Assert.Equal(1, Count(Quote(Make("Small Life Flask"), Make("Small Mana Flask"), Make("Orb of Fusing")), "Small Hybrid Flask"));
    }

    [Fact]
    public void Mirror_downgrade_is_a_multi_currency_quote()
    {
        var quote = Quote(Make("Mirror of Kalandra"));
        Assert.Equal(2, Count(quote, "Exalted Orb"));
        Assert.Equal(5, Count(quote, "Regal Orb"));
        Assert.Equal(1, Count(quote, "Divine Orb"));
    }

    [Theory]
    [InlineData("Driftwood Wand", "SpellDamageOnWeapon2")]
    [InlineData("Gnarled Branch", "SpellDamageOnTwoHandWeapon2")]
    public void Chain_belt_craft_uses_the_correct_one_or_two_handed_spell_modifier(string name, string id)
    {
        var output = Assert.Single(Quote(Make(name), Make("Chain Belt", ItemRarity.Rare), Make("Blacksmith's Whetstone")).Rewards);
        Assert.Contains(output.Mods, m => Catalog.Mods.ByHash(m.Hash)!.Id == id);
    }

    [Fact]
    public void Weapon_and_boot_crafts_consume_all_ingredients_and_keep_sockets()
    {
        var weapon = Make("Rusted Sword") with { Sockets = [new(SocketColour.Red)] };
        var result = Assert.Single(Quote(weapon, Make("Rustic Sash", ItemRarity.Rare), Make("Blacksmith's Whetstone")).Rewards);
        Assert.Equal(ItemRarity.Magic, result.Rarity);
        Assert.Contains(result.Mods, m => Catalog.Mods.ByHash(m.Hash)!.Id == "LocalIncreasedPhysicalDamagePercent2");
        Assert.Single(result.Sockets);
        var boots = Assert.Single(Quote(Make("Iron Greaves"), Make("Quicksilver Flask"), Make("Orb of Augmentation")).Rewards);
        Assert.Contains(boots.Mods, m => Catalog.Mods.ByHash(m.Hash)!.Id == "MovementVelocity1");
    }
}

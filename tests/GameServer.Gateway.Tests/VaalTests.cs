using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class VaalTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

    static Item? Apply(Item item, int seed = 1, ItemCatalog.BaseType? type = null)
    {
        var t = type ?? Program.Items.ByHash(item.TypeHash)!;
        return CurrencyEffects.Vaal(item, t, Program.Items.Requirements(t), Program.Rolls, new Random(seed));
    }

    static Item SixModRare()
    {
        var item = Program.Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 84 }, ItemRarity.Rare,
            new Random(5), shape: (6, 3));
        Assert.Equal(6, item.Mods.Count(m => m.Section == ModSection.Explicit));
        return item with { NameWords = [111u, 222u] };
    }

    static int Seed(bool reroll) =>
        Enumerable.Range(0, 1000).First(seed => (new Random(seed).Next(100) >= 90) == reroll);

    [Fact]
    public void A_normal_weapon_becomes_a_corrupted_rare()
    {
        var after = Apply(ItemCatalog.Create(Wand) with { ItemLevel = 84, Quality = 17 })!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Corrupted);
        Assert.True(after.Identified);
        Assert.Equal(17, after.Quality);
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
        Assert.Equal(2, after.NameWords.Count);
    }

    [Fact]
    public void A_magic_weapon_becomes_a_corrupted_rare()
    {
        var magic = ItemCatalog.Create(Wand) with { ItemLevel = 84, Rarity = ItemRarity.Magic };
        var after = Apply(magic)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.True(after.Corrupted);
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
    }

    [Fact]
    public void A_rare_replacement_keeps_the_name_and_the_affix_count()
    {
        var before = SixModRare();
        var after = Apply(before, Seed(reroll: false))!;
        Assert.True(after.Corrupted);
        Assert.Equal(before.NameWords, after.NameWords);
        Assert.Equal(6, after.Mods.Count(m => m.Section == ModSection.Explicit));
    }

    [Fact]
    public void A_rare_full_reroll_gets_a_new_name_and_four_to_six_affixes()
    {
        var before = SixModRare();
        var after = Apply(before, Seed(reroll: true))!;
        Assert.True(after.Corrupted);
        Assert.Equal(2, after.NameWords.Count);
        Assert.NotEqual(before.NameWords, after.NameWords);
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
    }

    [Fact]
    public void One_white_socket_is_coloured_and_links_quality_and_implicits_stay()
    {
        var before = ItemCatalog.Create(Wand) with
        {
            ItemLevel = 84,
            Quality = 17,
            Sockets = [new(SocketColour.White, null), new(SocketColour.White, null), new(SocketColour.White, null)],
            Links = [3],
        };
        var after = Apply(before)!;
        Assert.Equal(3, after.Sockets.Count);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(17, after.Quality);
        Assert.Equal(2, after.Sockets.Count(s => s.Colour == SocketColour.White));
        Assert.Equal(before.Mods, after.Mods.Where(m => m.Section != ModSection.Explicit));
    }

    [Fact]
    public void A_corrupted_item_is_refused()
    {
        Assert.Null(Apply(ItemCatalog.Create(Wand) with { ItemLevel = 84, Corrupted = true }));
        Assert.Null(Apply(SixModRare() with { Corrupted = true }));
    }

    [Fact]
    public void A_mirrored_weapon_is_refused()
    {
        var item = ItemCatalog.Create(Wand) with { ItemLevel = 84, Mirrored = true };
        Assert.Null(Apply(item));
    }

    [Fact]
    public void A_unique_item_is_refused()
    {
        Assert.Null(Apply(ItemCatalog.Create(Wand) with { ItemLevel = 84, Rarity = ItemRarity.Unique }));
        Assert.Null(Apply(SixModRare() with { Rarity = ItemRarity.Unique }));
    }

    [Fact]
    public void An_unsupported_weapon_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/StormBladeOneHand")!;
        Assert.False(type.Supported);
        Assert.Null(Apply(ItemCatalog.Create(type) with { ItemLevel = 84 }, type: type));
    }

    [Fact]
    public void An_empty_pool_is_refused()
    {
        var item = ItemCatalog.Create(Wand) with { ItemLevel = 0 };
        Assert.Empty(Program.Rolls.Pool(Wand, 0));
        Assert.Null(Apply(item));
    }

    [Theory]
    [InlineData("Metadata/Items/Jewels/JewelStr")]
    [InlineData("Metadata/Items/Flasks/FlaskLife1")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    public void Non_weapon_armour_and_jewellery_targets_are_refused(string id)
    {
        var type = Program.Items.Find(id)!;
        Assert.Null(Apply(ItemCatalog.Create(type) with { ItemLevel = 84 }, type: type));
    }

    [Fact]
    public void Unknown_no_Mods_and_missing_data_targets_are_refused()
    {
        Assert.Null(Apply(ItemCatalog.Create(Wand) with { ItemLevel = 84, TypeHash = 0xdeadbeef }, type: Wand));
        Assert.Null(Apply(ItemCatalog.Create(Wand) with { ItemLevel = 84, Components = ["Base", "Weapon"] }, type: Wand));
    }
}

using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FractureTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

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

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Fracture(item, Program.Rolls, new Random(seed));

    [Fact]
    public void A_four_mod_rare_gets_one_random_explicit_fractured_first()
    {
        var before = FourModWand() with { Quality = 17 };
        var after = Apply(before)!;

        Assert.Equal(1, after.FracturedCount);
        var explicitBefore = before.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
        var explicitAfter = after.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
        Assert.Equal(explicitBefore.Count, explicitAfter.Count);
        Assert.True(explicitBefore.ToHashSet().SetEquals(explicitAfter));

        int firstExplicit = after.Mods.Select((m, i) => (m, i)).First(p => p.m.Section == ModSection.Explicit).i;
        Assert.Equal(firstExplicit, after.FracturedIndexes().Single());
        Assert.Contains(after.Mods[firstExplicit], explicitBefore);

        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.Equal(17, after.Quality);
        Assert.Equal(before.NameWords, after.NameWords);
    }

    [Fact]
    public void The_fractured_mod_is_one_of_the_original_explicits_and_its_values_do_not_change()
    {
        var before = FourModWand();
        var after = Apply(before, seed: 7)!;
        var fractured = after.Mods[after.FracturedIndexes().Single()];
        Assert.Contains(fractured, before.Mods.Where(m => m.Section == ModSection.Explicit));
        Assert.Equal(before.Mods.Single(m => m == fractured), fractured);
    }

    [Fact]
    public void A_rare_with_only_three_explicits_is_refused()
    {
        var item = Rare(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void An_already_fractured_item_is_refused() =>
        Assert.Null(Apply(FourModWand() with { FracturedCount = 1 }));

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Unique)]
    public void Non_rare_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(FourModWand() with { Rarity = rarity }));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Corrupted_or_mirrored_items_are_refused(bool corrupted, bool mirrored) =>
        Assert.Null(Apply(FourModWand() with { Corrupted = corrupted, Mirrored = mirrored }));

    [Fact]
    public void An_unmodifiable_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.True(type.Unmodifiable);
        Assert.Null(Apply(Rare(type, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1"))));
    }

    [Fact]
    public void A_class_the_game_cannot_fracture_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/AtlasUpgrades/AtlasRegionUpgrade1_2")!;
        Assert.False(type.CanBeFractured);
        Assert.Null(Apply(Rare(type, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1"))));
    }

    [Fact]
    public void An_unknown_or_no_Mods_target_is_refused()
    {
        Assert.Null(Apply(FourModWand() with { TypeHash = 0xdeadbeef }));
        Assert.Null(Apply(FourModWand() with { Components = ["Base", "Weapon"] }));
    }
}

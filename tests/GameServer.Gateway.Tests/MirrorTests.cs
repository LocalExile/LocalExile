using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MirrorTests
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

    static Item? Apply(Item item) => CurrencyEffects.Mirror(item, Program.Rolls);

    [Fact]
    public void A_rare_wand_copy_is_mirrored_and_keeps_every_property()
    {
        var before = FourModWand() with
        {
            Quality = 17,
            Charges = 23,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            FracturedCount = 1,
        };
        var copy = Apply(before)!;

        Assert.True(copy.Mirrored);
        Assert.False(before.Mirrored);
        Assert.Equal(before.TypeHash, copy.TypeHash);
        Assert.Equal(before.Components, copy.Components);
        Assert.Equal(before.VisualIdentity, copy.VisualIdentity);
        Assert.Equal(before.Rarity, copy.Rarity);
        Assert.Equal(before.Identified, copy.Identified);
        Assert.Equal(before.Quality, copy.Quality);
        Assert.Equal(before.Charges, copy.Charges);
        Assert.Equal(before.Sockets, copy.Sockets);
        Assert.Equal(before.Links, copy.Links);
        Assert.Equal(before.Mods, copy.Mods);
        Assert.Equal(before.NameWords, copy.NameWords);
        Assert.Equal(before.FracturedCount, copy.FracturedCount);
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Rare)]
    public void Non_unique_equipment_is_mirrorable(ItemRarity rarity) =>
        Assert.NotNull(Apply(FourModWand() with { Rarity = rarity }));

    [Fact]
    public void A_unique_item_is_refused() =>
        Assert.Null(Apply(FourModWand() with { Rarity = ItemRarity.Unique }));

    [Theory]
    [InlineData("Metadata/Items/Flasks/FlaskUtility1")]
    [InlineData("Metadata/Items/Jewels/JewelStr")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    public void Non_equipment_is_refused(string id)
    {
        var type = Program.Items.Find(id)!;
        Assert.False(type.CanBeMirrored);
        Assert.Null(Apply(Rare(type, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1"))));
    }

    [Fact]
    public void A_corrupted_item_is_refused() =>
        Assert.Null(Apply(FourModWand() with { Corrupted = true }));

    [Fact]
    public void An_already_mirrored_item_is_refused() =>
        Assert.Null(Apply(FourModWand() with { Mirrored = true }));

    [Fact]
    public void An_unmodifiable_base_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.True(type.Unmodifiable);
        Assert.Null(Apply(Rare(type, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1"))));
    }

    [Fact]
    public void An_unknown_or_no_Mods_target_is_refused()
    {
        Assert.Null(Apply(FourModWand() with { TypeHash = 0xdeadbeef }));
        Assert.Null(Apply(FourModWand() with { Components = ["Base", "Weapon"] }));
    }

    [Fact]
    public void A_fractured_item_can_be_mirrored_and_keeps_its_fractured_mods()
    {
        var before = FourModWand() with { FracturedCount = 1 };
        var copy = Apply(before)!;
        Assert.True(copy.Mirrored);
        Assert.Equal(1, copy.FracturedCount);
        Assert.Equal(before.FracturedIndexes(), copy.FracturedIndexes());
        Assert.Equal(before.Mods, copy.Mods);
    }
}

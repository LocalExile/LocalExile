using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AnnulmentTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;
    static readonly ItemCatalog.BaseType Jewel = Program.Items.Find("Metadata/Items/Jewels/JewelStr")!;

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Magic(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Magic,
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item Rare(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Annulment(item, Program.Rolls, new Random(seed));

    [Fact]
    public void A_magic_item_loses_its_only_affix_and_stays_magic()
    {
        var before = Magic(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1"));
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Magic, after.Rarity);
        Assert.DoesNotContain(after.Mods, m => m.Section == ModSection.Explicit);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
    }

    [Fact]
    public void A_rare_item_loses_one_affix_and_keeps_its_name()
    {
        var before = Rare(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1"));
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Equal(before.NameWords, after.NameWords);
        Assert.Equal(3, after.Mods.Count(m => m.Section == ModSection.Explicit));
    }

    [Fact]
    public void A_rare_item_can_drop_to_one_mod_and_stays_rare()
    {
        var before = Rare(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1"));
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Single(after.Mods, m => m.Section == ModSection.Explicit);
    }

    [Fact]
    public void A_rare_jewel_loses_one_mod()
    {
        var before = Program.Rolls.Roll(ItemCatalog.Create(Jewel) with { ItemLevel = 84 }, ItemRarity.Rare,
            new Random(5), shape: (4, 2));
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Equal(3, after.Mods.Count(m => m.Section == ModSection.Explicit));
    }

    [Fact]
    public void One_random_explicit_is_removed()
    {
        var before = Rare(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1"));
        var after = Apply(before)!;
        Assert.Equal(before.Mods.Count - 1, after.Mods.Count);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
    }

    [Fact]
    public void A_prefix_lock_never_removes_the_locked_prefix()
    {
        var craft = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == "item_generation_cannot_change_prefixes"));
        var prefix = Explicit("LocalIncreasedPhysicalDamagePercent1");
        var item = Rare(Wand, prefix, Explicit("LocalIncreasedAttackSpeed1")) with
        {
            Mods = [.. Rare(Wand, prefix, Explicit("LocalIncreasedAttackSpeed1")).Mods,
                craft.Roll(ModSection.Explicit, new Random(3))],
        };
        for (int seed = 0; seed < 100; seed++)
            Assert.Contains(prefix, Apply(item, seed)!.Mods);
    }

    [Fact]
    public void A_cannot_roll_attack_craft_protects_attack_affixes()
    {
        var craft = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == "item_generation_cannot_roll_attack_affixes"));
        var pool = Program.Rolls.Pool(Wand, 84);
        var attack = pool.First(p => p.Mod.Tags.Contains("attack")).Mod.Roll(ModSection.Explicit, new Random(4));
        var other = pool.First(p => !p.Mod.Tags.Contains("attack") && !p.Mod.Families.Intersect(
            pool.First(a => a.Mod.Tags.Contains("attack")).Mod.Families).Any()).Mod.Roll(ModSection.Explicit, new Random(5));
        var item = Rare(Wand, attack, other) with
        {
            Mods = [.. Rare(Wand, attack, other).Mods, craft.Roll(ModSection.Explicit, new Random(6))],
        };
        for (int seed = 0; seed < 100; seed++)
            Assert.Contains(attack, Apply(item, seed)!.Mods);
    }

    [Fact]
    public void A_fractured_affix_is_never_removed()
    {
        var before = Program.Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 84 }, ItemRarity.Rare,
            new Random(7), shape: (4, 3)) with { FracturedCount = 1 };
        var fractured = before.Mods.First(m => m.Section == ModSection.Explicit);
        for (int seed = 0; seed < 100; seed++)
            Assert.Contains(fractured, Apply(before, seed)!.Mods);
    }

    [Fact]
    public void A_rare_base_without_an_eligible_explicit_is_refused()
    {
        var item = Rare(Wand) with { Mods = [new(ModSection.Implicit, 108, [15])] };
        Assert.Null(Apply(item));
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Unique)]
    public void Non_magic_and_non_rare_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(Rare(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Rarity = rarity }));

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
    public void A_corrupted_magic_item_is_refused() =>
        Assert.Null(Apply(Magic(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Corrupted = true }));

    [Fact]
    public void A_mirrored_magic_item_is_refused() =>
        Assert.Null(Apply(Magic(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Mirrored = true }));

    [Fact]
    public void An_unsupported_magic_item_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/StormBladeOneHand")!;
        Assert.False(type.Supported);
        var mod = Program.Rolls.Pool(type, 84).First().Mod.Roll(ModSection.Explicit, new Random(1));
        Assert.Null(Apply(Magic(type, mod)));
    }

    [Fact]
    public void An_unknown_or_no_Mods_base_is_refused()
    {
        Assert.Null(Apply(Magic(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1")) with { TypeHash = 0xdeadbeef }));
        Assert.Null(Apply(Magic(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1")) with { Components = ["Base"] }));
    }

    [Fact]
    public void Annulment_preserves_non_explicit_mods_and_other_properties()
    {
        var before = Rare(Wand, Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1")) with
        {
            Quality = 17,
            Charges = 23,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            Mods = [new(ModSection.Implicit, 108, [15]), new(ModSection.Enchant, 108, [19]),
                new(ModSection.Explicit, Mod("LocalIncreasedPhysicalDamagePercent1").Hash, [45]),
                new(ModSection.Explicit, Mod("LocalIncreasedAttackSpeed1").Hash, [6])],
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
        Assert.Single(after.Mods, m => m.Section == ModSection.Explicit);
    }
}

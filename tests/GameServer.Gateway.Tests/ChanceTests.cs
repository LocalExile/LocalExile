using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChanceTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;

    static Item Normal(ItemCatalog.BaseType type) => ItemCatalog.Create(type);

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Chance(item, Program.Rolls, new Random(seed));

    [Fact]
    public void A_normal_weapon_becomes_identified_magic_or_rare_from_the_documented_rule()
    {
        var random = new Random(41);
        int rare = 0;
        for (int i = 0; i < 500; i++)
        {
            var after = CurrencyEffects.Chance(Normal(Wand) with { ItemLevel = 84 }, Program.Rolls, random)!;
            Assert.True(after.Identified);
            var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
            var mods = explicits.Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.Equal(mods.SelectMany(m => m.Families).Count(), mods.SelectMany(m => m.Families).Distinct().Count());
            foreach (var (mod, rolled) in mods.Zip(explicits))
            {
                Assert.Equal(Wand.ModDomain, mod.Domain);
                Assert.True(mod.WeightFor(Wand.Tags) > 0);
                for (int stat = 0; stat < mod.Stats.Count; stat++)
                    Assert.InRange(rolled.Values[stat], Math.Min(mod.Stats[stat].Min, mod.Stats[stat].Max),
                        Math.Max(mod.Stats[stat].Min, mod.Stats[stat].Max));
            }
            if (after.Rarity == ItemRarity.Rare)
            {
                rare++;
                Assert.InRange(explicits.Length, 4, 6);
                Assert.Equal(2, after.NameWords.Count);
                Assert.All(new[] { ItemRolls.Prefix, ItemRolls.Suffix },
                    side => Assert.InRange(mods.Count(m => m.GenerationType == side), 1, 3));
            }
            else
            {
                Assert.Equal(ItemRarity.Magic, after.Rarity);
                Assert.InRange(explicits.Length, 2, 4);
                Assert.Empty(after.NameWords);
                Assert.All(new[] { ItemRolls.Prefix, ItemRolls.Suffix },
                    side => Assert.InRange(mods.Count(m => m.GenerationType == side), 0, 2));
            }
        }
        Assert.InRange(rare, 120, 180);
    }

    [Fact]
    public void The_rare_share_is_the_documented_thirty_percent()
    {
        var random = new Random(11);
        const int trials = 5000;
        int rare = 0;
        for (int i = 0; i < trials; i++)
            if (CurrencyEffects.Chance(Normal(Wand) with { ItemLevel = 84 }, Program.Rolls, random)!.Rarity == ItemRarity.Rare)
                rare++;
        Assert.InRange((double)rare / trials, 0.28, 0.32);
    }

    [Fact]
    public void Chance_preserves_non_explicit_mods_and_other_properties()
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
        Assert.True(after.Identified);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.Charges, after.Charges);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods, after.Mods.Where(m => m.Section != ModSection.Explicit));
    }

    [Theory]
    [InlineData("Metadata/Items/Jewels/JewelStr")]
    [InlineData("Metadata/Items/Flasks/FlaskLife1")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    [InlineData("Metadata/Items/Rings/Ring2")]
    public void Non_weapon_and_armour_targets_are_refused(string id)
    {
        var type = Program.Items.Find(id)!;
        Assert.Null(Apply(ItemCatalog.Create(type) with { ItemLevel = 84 }));
    }

    [Theory]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void Non_normal_items_are_refused(ItemRarity rarity) =>
        Assert.Null(Apply(Normal(Wand) with { ItemLevel = 84, Rarity = rarity }));

    [Fact]
    public void A_mirrored_weapon_is_refused()
    {
        var item = Normal(Wand) with { ItemLevel = 84, Mirrored = true };
        Assert.Null(Apply(item));
    }

    [Fact]
    public void A_corrupted_weapon_is_refused()
    {
        var item = Normal(Wand) with { ItemLevel = 84, Corrupted = true };
        Assert.Null(Apply(item));
    }

    [Fact]
    public void An_unsupported_weapon_is_refused()
    {
        var type = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/StormBladeOneHand")!;
        Assert.False(type.Supported);
        Assert.Null(Apply(ItemCatalog.Create(type) with { ItemLevel = 84 }));
    }

    [Fact]
    public void An_empty_pool_is_refused()
    {
        var item = Normal(Wand) with { ItemLevel = 0 };
        Assert.Empty(Program.Rolls.Pool(Wand, 0));
        Assert.Null(Apply(item));
    }

    [Fact]
    public void Unknown_and_no_Mods_targets_are_refused()
    {
        Assert.Null(Apply(Normal(Wand) with { ItemLevel = 84, TypeHash = 0xdeadbeef }));
        Assert.Null(Apply(Normal(Wand) with { ItemLevel = 84, Components = ["Base", "Weapon"] }));
    }
}

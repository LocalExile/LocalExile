using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AlterationTests
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

    static Item? Apply(Item item, int seed = 1) => CurrencyEffects.Alteration(item, Program.Rolls, new Random(seed));

    [Fact]
    public void An_empty_pool_is_refused_instead_of_stripping_the_item()
    {
        var item = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1")) with { ItemLevel = 0 };
        Assert.Empty(Program.Rolls.Pool(Wand, 0));
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
        Assert.Null(Apply(Magic(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollMagic")!)));
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
    public void The_reroll_count_is_50_50_and_each_side_appears_at_most_once()
    {
        // CoE beta 3.29.3.1.2: 300 applies gave 151 single-affix and 149 double-affix items.
        var random = new Random(317);
        int singles = 0;
        for (int i = 0; i < 10000; i++)
        {
            var after = CurrencyEffects.Alteration(Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"),
                Explicit("LocalIncreasedAttackSpeed1")), Program.Rolls, random)!;
            Assert.Equal(ItemRarity.Magic, after.Rarity);
            Assert.True(after.Identified);
            Assert.Empty(after.NameWords);
            var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
            Assert.InRange(explicits.Length, 1, 2);
            var mods = explicits.Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
            Assert.Equal(mods.Length, mods.Select(m => m.GenerationType).Distinct().Count());
            var families = mods.SelectMany(m => m.Families).ToArray();
            Assert.Equal(families.Length, families.Distinct().Count());
            foreach (var (mod, rolled) in mods.Zip(explicits))
            {
                Assert.Equal(Wand.ModDomain, mod.Domain);
                Assert.True(mod.WeightFor(Wand.Tags) > 0);
                Assert.Equal(mod.Stats.Count, rolled.Values.Count);
                for (int stat = 0; stat < mod.Stats.Count; stat++)
                    Assert.InRange(rolled.Values[stat], Math.Min(mod.Stats[stat].Min, mod.Stats[stat].Max),
                        Math.Max(mod.Stats[stat].Min, mod.Stats[stat].Max));
            }
            if (explicits.Length == 1) singles++;
        }
        Assert.InRange(singles, 4800, 5200);
    }

    [Fact]
    public void A_magic_flask_rerolls_one_prefix_from_the_two_flask_mods()
    {
        var flask = Program.Items.Find("Small Life Flask")!;
        var random = new Random(19);
        for (int i = 0; i < 100; i++)
        {
            var after = CurrencyEffects.Alteration(Magic(flask, Explicit("FlaskIncreasedRecoverySpeed1")),
                Program.Rolls, random)!;
            var explicitMod = Assert.Single(after.Mods, m => m.Section == ModSection.Explicit);
            var mod = Program.Items.Mods.ByHash(explicitMod.Hash)!;
            Assert.Equal(ItemRolls.Prefix, mod.GenerationType);
            Assert.Contains(mod.Id, new[] { "FlaskIncreasedRecoveryAmount1", "FlaskIncreasedRecoverySpeed1" });
        }
    }

    [Fact]
    public void Alteration_preserves_non_explicit_mods_and_other_properties()
    {
        var before = Magic(Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("LocalIncreasedAttackSpeed1")) with
        {
            Quality = 17,
            Charges = 23,
            Identified = false,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            Mods = [new(ModSection.Implicit, 108, [15]), new(ModSection.Enchant, 108, [19]),
                new(ModSection.Explicit, Mod("LocalIncreasedPhysicalDamagePercent1").Hash, [45]),
                new(ModSection.Explicit, Mod("LocalIncreasedAttackSpeed1").Hash, [6])],
        };
        var after = Apply(before)!;
        Assert.Equal(ItemRarity.Magic, after.Rarity);
        Assert.True(after.Identified);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.Charges, after.Charges);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Explicit),
            after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 1, 2);
    }
}

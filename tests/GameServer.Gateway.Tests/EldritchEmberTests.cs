using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class EldritchEmberTests
{
    static readonly ItemCatalog.BaseType Helmet = Program.Items.Find("HelmetStr1")!;
    static readonly ItemCatalog.BaseType RitualHelmet = Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStrDexRitual1")!;
    static readonly ItemCatalog.BaseType Gloves = Program.Items.Find("GlovesStr1")!;
    static readonly ItemCatalog.BaseType Boots = Program.Items.Find("BootsStr1")!;
    static readonly ItemCatalog.BaseType Body = Program.Items.Find("BodyStr1")!;

    static ItemMod AnyExplicit(ItemCatalog.BaseType type) =>
        Program.Items.Mods.All.First(m => m.Domain == type.ModDomain && m.GenerationType == ItemRolls.Prefix &&
            m.Level <= 84).Roll(ModSection.Explicit, new Random(1));

    static Item? Apply(Item item, ItemCatalog.BaseType type, int tier, int seed = 1) =>
        CurrencyEffects.EldritchEmber(item, type, tier, Program.Rolls, new Random(seed));

    static ItemMod Exarch(Item item) =>
        item.Mods.Single(m => m.Section == ModSection.Implicit &&
            Program.Items.Mods.ByHash(m.Hash)?.GenerationType == ItemRolls.ExarchImplicit);

    [Fact]
    public void A_lesser_ember_adds_a_tier_one_exarch_implicit()
    {
        var before = ItemCatalog.Create(Helmet);
        var after = Apply(before, Helmet, 1)!;
        Assert.Equal(ItemRarity.Normal, after.Rarity);
        var mod = Program.Items.Mods.ByHash(Exarch(after).Hash)!;
        Assert.Equal(ItemRolls.ExarchImplicit, mod.GenerationType);
        Assert.EndsWith("1", mod.Id);
        Assert.Single(after.Mods, m => m.Section == ModSection.Implicit);
    }

    [Fact]
    public void The_four_tiers_add_their_own_variant()
    {
        for (int tier = 1; tier <= 4; tier++)
        {
            var after = Apply(ItemCatalog.Create(Helmet) with { Rarity = ItemRarity.Rare }, Helmet, tier)!;
            Assert.EndsWith(tier.ToString(), Program.Items.Mods.ByHash(Exarch(after).Hash)!.Id);
        }
    }

    [Fact]
    public void Normal_implicits_are_removed_and_eater_implicits_stay()
    {
        var eater = Program.Items.Mods.All.First(m => m.GenerationType == ItemRolls.EaterImplicit &&
            m.WeightFor(RitualHelmet.Tags) > 0);
        var before = ItemCatalog.Create(RitualHelmet) with
        {
            Rarity = ItemRarity.Rare,
            Mods = [.. ItemCatalog.Create(RitualHelmet).Mods, eater.Roll(ModSection.Implicit, new Random(2))],
        };
        var normal = ItemCatalog.Create(RitualHelmet).Mods.First(m => m.Section == ModSection.Implicit);
        var after = Apply(before, RitualHelmet, 1)!;
        Assert.DoesNotContain(after.Mods, m => m.Section == ModSection.Implicit && m.Hash == normal.Hash);
        Assert.Contains(after.Mods, m => m.Section == ModSection.Implicit && m.Hash == eater.Hash);
        Assert.Equal(2, after.Mods.Count(m => m.Section == ModSection.Implicit));
    }

    [Fact]
    public void An_existing_exarch_implicit_is_replaced()
    {
        var first = Apply(ItemCatalog.Create(Helmet) with { Rarity = ItemRarity.Rare }, Helmet, 1)!;
        var second = Apply(first, Helmet, 3)!;
        Assert.EndsWith("3", Program.Items.Mods.ByHash(Exarch(second).Hash)!.Id);
        Assert.Single(second.Mods, m => m.Section == ModSection.Implicit);
    }

    [Fact]
    public void Non_armour_targets_are_refused()
    {
        foreach (string id in new[] { "Metadata/Items/Rings/Ring2", "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1",
            "Metadata/Items/Armours/Shields/ShieldStr1", "Metadata/Items/Flasks/FlaskLife1",
            "Metadata/Items/Maps/MapTier1_1" })
        {
            var type = Program.Items.Find(id)!;
            Assert.Null(Apply(ItemCatalog.Create(type) with { Rarity = ItemRarity.Rare }, type, 1));
        }
    }

    [Fact]
    public void Unique_corrupted_and_mirrored_items_are_refused()
    {
        Assert.Null(Apply(ItemCatalog.Create(Helmet) with { Rarity = ItemRarity.Unique }, Helmet, 1));
        Assert.Null(Apply(ItemCatalog.Create(Helmet) with { Corrupted = true }, Helmet, 1));
        Assert.Null(Apply(ItemCatalog.Create(Helmet) with { Mirrored = true }, Helmet, 1));
    }

    [Fact]
    public void Unknown_and_no_Mods_bases_are_refused()
    {
        Assert.Null(Apply(ItemCatalog.Create(Helmet) with { TypeHash = 0xdeadbeef }, Helmet, 1));
        Assert.Null(Apply(ItemCatalog.Create(Helmet) with { Components = ["Base"] }, Helmet, 1));
    }

    [Fact]
    public void The_ember_preserves_explicits_quality_sockets_links_and_name()
    {
        var before = ItemCatalog.Create(Helmet) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Quality = 17,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))],
            Links = [1],
            Mods = [.. ItemCatalog.Create(Helmet).Mods, AnyExplicit(Helmet)],
        };
        var after = Apply(before, Helmet, 1)!;
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Equal(before.NameWords, after.NameWords);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Mods.Where(m => m.Section != ModSection.Implicit),
            after.Mods.Where(m => m.Section != ModSection.Implicit));
    }

    [Fact]
    public void The_pools_match_the_coe_observed_base_mods()
    {
        Assert.Contains(Program.Rolls.EldritchEmberPool(Helmet, 1), p => p.Mod.Id == "IncreasedCastSpeedEldritchImplicit1");
        Assert.Contains(Program.Rolls.EldritchEmberPool(Gloves, 1), p => p.Mod.Id == "IncreasedAttackSpeedEldritchImplicit1");
        Assert.Contains(Program.Rolls.EldritchEmberPool(Boots, 1), p => p.Mod.Id == "MovementVelocityEldritchImplicit1");
        Assert.Contains(Program.Rolls.EldritchEmberPool(Body, 1), p => p.Mod.Id == "SpellCriticalStrikeMultiplierEldritchImplicit1");
        var tier3 = Program.Rolls.EldritchEmberPool(Helmet, 3);
        Assert.Contains(tier3, p => p.Mod.Id.EndsWith("UniquePresence3"));
        Assert.Contains(tier3, p => p.Mod.Id.EndsWith("PinnaclePresence3"));
        Assert.All(Program.Rolls.EldritchEmberPool(Helmet, 1), p =>
        {
            Assert.Equal(ItemRolls.ExarchImplicit, p.Mod.GenerationType);
            Assert.True(p.Weight > 0);
        });
    }
}

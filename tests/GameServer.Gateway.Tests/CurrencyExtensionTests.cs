using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The currency added after the first pass: the map chisels (CurrencyItems Action add_map_alt_quality), the two
/// Eldritch orbs and Orb of Unmaking's admission.</summary>
public class CurrencyExtensionTests
{
    static ItemRolls Rolls => new(Program.Items);

    static ItemCatalog.BaseType Chisel => Program.Items.Find(CurrencyEffects.CartographersChisel)!;
    static ItemCatalog.BaseType Map => Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
    static ItemCatalog.BaseType Helmet => Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!;

    static Item Rare(ItemCatalog.BaseType type) => ItemCatalog.Create(type) with
    {
        Rarity = ItemRarity.Rare,
        NameWords = [111u, 222u],
        ItemLevel = 70,
    };

    [Fact]
    public void A_chisel_adds_map_quality_and_refuses_anything_else()
    {
        byte qualityType = Program.Items.AlternateQualityOf(Chisel)!.Value;
        var map = ItemCatalog.Create(Map);
        var once = CurrencyEffects.Chisel(map, Map, qualityType);
        Assert.NotNull(once);
        Assert.Equal(20, once!.Quality);
        Assert.Equal(20, CurrencyEffects.Chisel(map with { Rarity = ItemRarity.Unique }, Map, qualityType)!.Quality);
        Assert.Equal(qualityType, once.AlternateQualityType);
        Assert.Equal(20, CurrencyEffects.Chisel(map with { Quality = 20, AlternateQualityType = qualityType }, Map, qualityType) is null ? 20 : 0);

        // Its table action is the one the effect dispatcher claims, and a chisel is not a map.
        Assert.Equal("add_map_alt_quality", Program.Items.CurrencyAction(Chisel));
        Assert.Null(CurrencyEffects.Chisel(ItemCatalog.Create(Helmet), Helmet, qualityType));
        Assert.True(CurrencyEffects.TryAction(Program.Items, Rolls, map, Chisel, new Random(1), out var viaTable));
        Assert.NotNull(viaTable);
    }

    [Fact]
    public void Eldritch_orbs_need_an_eldritch_implicit_and_keep_it()
    {
        var armour = Rare(Helmet);
        Assert.False(Rolls.HasEldritchImplicit(armour));
        Assert.Null(CurrencyEffects.EldritchChaos(armour, Rolls, new Random(1)));
        Assert.Null(CurrencyEffects.EldritchExalted(armour, Rolls, new Random(1)));

        var ichor = Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!;
        Assert.NotNull(ichor);
        var marked = Rolls.EldritchIchor(armour, Helmet, 1, new Random(1));
        Assert.NotNull(marked);
        Assert.True(Rolls.HasEldritchImplicit(marked!));
        var implicitMod = marked.Mods.Where(m => m.Section == ModSection.Implicit).ToList();
        Assert.NotEmpty(implicitMod);

        var chaos = CurrencyEffects.EldritchChaos(marked, Rolls, new Random(1));
        Assert.NotNull(chaos);
        Assert.Equal(implicitMod, chaos!.Mods.Where(m => m.Section == ModSection.Implicit).ToList());
        Assert.Equal(ItemRarity.Rare, chaos.Rarity);
    }
}

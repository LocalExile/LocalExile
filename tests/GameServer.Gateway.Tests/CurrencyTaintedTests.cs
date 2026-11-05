using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The second currency batch: Orb of Binding, the Eldritch Orb of Annulment and the Tainted (Scourge) family.</summary>
public class CurrencyTaintedTests
{
    static ItemRolls Rolls => new(Program.Items);

    static ItemCatalog.BaseType BodyArmour => Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
    static ItemCatalog.BaseType Helmet => Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!;

    static Item Rare(ItemCatalog.BaseType type, byte itemLevel = 70) =>
        Rolls.Alchemy(ItemCatalog.Create(type) with { ItemLevel = itemLevel, Identified = true }, new Random(7))!;

    [Fact]
    public void Binding_turns_a_normal_item_into_a_four_linked_socket_rare()
    {
        var requirements = Program.Items.Requirements(BodyArmour);
        var normal = ItemCatalog.Create(BodyArmour) with { ItemLevel = 70 };

        var bound = CurrencyEffects.Binding(normal, BodyArmour, Rolls, requirements, new Random(1));
        Assert.NotNull(bound);
        Assert.Equal(ItemRarity.Rare, bound!.Rarity);
        Assert.Equal(4, bound.Sockets.Count);
        Assert.Equal([4], bound.Links);
        Assert.All(bound.Sockets, s => Assert.Null(s.Gem));

        // Only normal items, only bases that can hold four sockets at their item level, never a corrupted one.
        Assert.Null(CurrencyEffects.Binding(Rare(BodyArmour), BodyArmour, Rolls, requirements, new Random(1)));
        Assert.Null(CurrencyEffects.Binding(normal with { Corrupted = true }, BodyArmour, Rolls, requirements, new Random(1)));
        Assert.Null(CurrencyEffects.Binding(normal with { ItemLevel = 1 }, BodyArmour, Rolls, requirements, new Random(1)));
        var ring = Program.Items.Find("Metadata/Items/Rings/Ring1")!;
        Assert.Null(CurrencyEffects.Binding(ItemCatalog.Create(ring), ring, Rolls, Program.Items.Requirements(ring), new Random(1)));
    }

    [Fact]
    public void Tainted_currency_needs_a_corrupted_item_and_the_Eldritch_orb_keeps_the_implicit()
    {
        var rare = Rare(Helmet);
        var requirements = Program.Items.Requirements(Helmet);
        Assert.Null(CurrencyEffects.Tainted(CurrencyEffects.TaintedChaosOrb, rare, Helmet, Rolls, requirements, new Random(1)));

        var corrupted = rare with { Corrupted = true };
        var chaos = CurrencyEffects.Tainted(CurrencyEffects.TaintedChaosOrb, corrupted, Helmet, Rolls, requirements, new Random(1));
        Assert.NotNull(chaos);
        Assert.True(chaos!.Corrupted);
        Assert.Equal(ItemRarity.Rare, chaos.Rarity);

        var scrap = CurrencyEffects.Tainted(CurrencyEffects.TaintedArmourersScrap, corrupted with { Quality = 0 }, Helmet, Rolls,
            requirements, new Random(1));
        Assert.NotNull(scrap);
        Assert.Equal(1, scrap!.Quality);
        Assert.True(scrap.Corrupted);

        // The Eldritch Orb of Annulment needs the Eldritch implicit, exactly like the other two Eldritch orbs: it removes
        // one explicit affix and the implicit stays.
        Assert.Null(CurrencyEffects.EldritchAnnulment(rare, Rolls, new Random(1)));
        var marked = Rolls.EldritchIchor(rare, Helmet, 1, new Random(1))!;
        int implicits = marked.Mods.Count(m => m.Section == ModSection.Implicit);
        int explicits = marked.Mods.Count(m => m.Section == ModSection.Explicit);
        Assert.Equal(1, implicits);
        var annulled = CurrencyEffects.EldritchAnnulment(marked, Rolls, new Random(1));
        Assert.NotNull(annulled);
        Assert.Equal(1, annulled!.Mods.Count(m => m.Section == ModSection.Implicit));
        Assert.Equal(explicits - 1, annulled.Mods.Count(m => m.Section == ModSection.Explicit));
    }
}

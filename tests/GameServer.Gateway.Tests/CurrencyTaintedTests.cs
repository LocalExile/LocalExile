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
        Assert.Null(CurrencyEffects.Tainted(CurrencyEffects.TaintedChaosOrb, rare, Helmet, Rolls, new Random(1)));

        // Tainted Chaos: over many seeds both outcomes, a reroll that stays rare and a strip to a normal item with no
        // explicit mods; the item stays corrupted either way.
        var corrupted = rare with { Corrupted = true };
        var chaos = Enumerable.Range(0, 40).Select(seed =>
            CurrencyEffects.Tainted(CurrencyEffects.TaintedChaosOrb, corrupted, Helmet, Rolls, new Random(seed))!).ToArray();
        Assert.All(chaos, c => Assert.True(c.Corrupted));
        Assert.Contains(chaos, c => c.Rarity == ItemRarity.Rare && c.Mods.Any(m => m.Section == ModSection.Explicit));
        Assert.Contains(chaos, c => c.Rarity == ItemRarity.Normal && c.Mods.All(m => m.Section != ModSection.Explicit)
            && c.NameWords.Count == 0);

        // Tainted Exalted: one mod more or one less, the item stays rare.
        int explicitCount = corrupted.Mods.Count(m => m.Section == ModSection.Explicit);
        var exalted = Enumerable.Range(0, 40).Select(seed =>
            CurrencyEffects.Tainted(CurrencyEffects.TaintedExaltedOrb, corrupted, Helmet, Rolls, new Random(seed))!).ToArray();
        Assert.All(exalted, e => Assert.Equal(ItemRarity.Rare, e.Rarity));
        Assert.Contains(exalted, e => e.Mods.Count(m => m.Section == ModSection.Explicit) == explicitCount - 1);
        if (explicitCount < 6)
            Assert.Contains(exalted, e => e.Mods.Count(m => m.Section == ModSection.Explicit) == explicitCount + 1);

        // Quality is randomised within 0-20%, not raised by one.
        var qualities = Enumerable.Range(0, 60).Select(seed => CurrencyEffects.Tainted(CurrencyEffects.TaintedArmourersScrap,
            corrupted with { Quality = 5 }, Helmet, Rolls, new Random(seed))!.Quality).Distinct().ToArray();
        Assert.All(qualities, q => Assert.InRange(q, 0, CurrencyEffects.MaximumQuality));
        Assert.True(qualities.Length > 5);

        // Jeweller's and Fusing: one socket or one link of the largest group, up or down. Three sockets, two linked.
        var socketed = corrupted with
        {
            Sockets = [new(SocketColour.Red), new(SocketColour.White), new(SocketColour.Blue)], Links = [2, 1],
        };
        var jewelled = Enumerable.Range(0, 40).Select(seed =>
            CurrencyEffects.Tainted(CurrencyEffects.TaintedJewellersOrb, socketed, Helmet, Rolls, new Random(seed))!).ToArray();
        Assert.Contains(jewelled, j => j.Sockets.Count == 4 && j.Sockets[3].Colour == SocketColour.White
            && j.Links!.SequenceEqual(new byte[] { 2, 1, 1 }));
        Assert.Contains(jewelled, j => j.Sockets.Count == 2 && j.Links!.SequenceEqual(new byte[] { 2 }));
        var fused = Enumerable.Range(0, 40).Select(seed =>
            CurrencyEffects.Tainted(CurrencyEffects.TaintedOrbOfFusing, socketed, Helmet, Rolls, new Random(seed))!.Links!.ToArray()).ToArray();
        Assert.All(fused, l => Assert.True(l.SequenceEqual(new byte[] { 3 }) || l.SequenceEqual(new byte[] { 1, 1, 1 })));
        Assert.Contains(fused, l => l.Length == 1);
        Assert.Contains(fused, l => l.Length == 3);

        foreach (byte[] layout in new byte[][] { [4, 2], [2, 4] })
        {
            var six = Rare(BodyArmour) with { Corrupted = true,
                Sockets = Enumerable.Range(0, 6).Select(_ => new ItemSocket(SocketColour.Red)).ToArray(), Links = layout };
            var outcomes = Enumerable.Range(0, 40).Select(seed => CurrencyEffects.Tainted(
                CurrencyEffects.TaintedOrbOfFusing, six, BodyArmour, Rolls, new Random(seed))!).ToArray();
            Assert.All(outcomes, item => Assert.Contains(item.Links!.Max(), new byte[] { 3, 5 }));
            Assert.Contains(outcomes, item => item.Links!.Max() == 5);
            Assert.All(outcomes, item => Assert.Equal(six.Sockets, item.Sockets));
            Assert.Null(CurrencyEffects.Tainted(CurrencyEffects.TaintedOrbOfFusing,
                six with { Links = [6] }, BodyArmour, Rolls, new Random(1)));
            Assert.Null(CurrencyEffects.Tainted(CurrencyEffects.TaintedOrbOfFusing,
                six with { Sockets = six.Sockets.Take(5).ToArray(), Links = [5] }, BodyArmour, Rolls, new Random(1)));
        }

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

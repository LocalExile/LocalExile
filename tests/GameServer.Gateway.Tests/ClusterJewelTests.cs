using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ClusterJewelTests
{
    [Fact]
    public void A_cluster_jewel_carries_its_theme_and_its_small_passives_and_affixes_follow_it()
    {
        var items = Program.Items;
        var rolls = new ItemRolls(items);
        var sizes = new[] { "Small Cluster Jewel", "Medium Cluster Jewel", "Large Cluster Jewel" }.Select(n => items.Find(n)!).ToList();

        // A new item of each base is a whole jewel: a theme of its size, a passive count in its range, its sockets.
        for (int seed = 0; seed < 20; seed++)
            foreach (var type in sizes)
            {
                var cluster = type.Cluster!;
                var made = ItemCatalog.Create(type, random: new Random(seed));
                var theme = ClusterJewels.ThemeOf(type, made)!;
                Assert.Equal((cluster.Size, false), (theme.Size, theme.Retired));
                Assert.InRange(Game.ClusterNodeBudget(made), cluster.MinNodes, cluster.MaxNodes);
                Assert.Equal(cluster.Size, Game.ClusterSocketCount(made));
                Assert.All(made.Mods, m => Assert.Equal(ModSection.Enchant, m.Section));
                Assert.Equal(cluster.Size == 0 ? 1 : 2, made.Mods.Count);
            }
        Assert.Equal([(2, 3), (4, 6), (8, 12)], sizes.Select(t => (t.Cluster!.MinNodes, t.Cluster.MaxNodes)));
        Assert.Null(items.Find("Cobalt Jewel")!.Cluster);

        // The enchantment's first value is the theme's row plus one, as the client reads it: 13 is Fire Damage.
        var large = sizes[2];
        var fire = ClusterJewels.FindTheme(large.Cluster!, "Fire Damage")!;
        Assert.Equal((12, 13, "affliction_fire_damage", "affliction_fire_damage"), (fire.Row, fire.Value, fire.Tag, fire.Passive));
        var notables = new[] { "AfflictionNotablePrismaticHeart__", "AfflictionNotableWidespreadDestruction" }.Select(id => items.Mods.ById(id)!).ToList();
        var jewel = ClusterJewels.Create(large, fire, 8, 2, [.. notables, items.Mods.ById("AfflictionJewelSmallPassivesGrantLife2_")!])!;
        Assert.Equal(ItemRarity.Rare, jewel.Rarity);
        Assert.Equal([13, 8], jewel.Mods[0].Values);
        Assert.Same(fire, ClusterJewels.ThemeOf(large, jewel));
        Assert.Equal((8, 2), (Game.ClusterNodeBudget(jewel), Game.ClusterSocketCount(jewel)));

        // Its notables are behind the theme's tag: they roll on this jewel, and not on one without a theme.
        var pool = rolls.Pool(large, jewel).Select(p => p.Mod.Id).ToList();
        Assert.Contains("AfflictionNotablePrismaticHeart__", pool);
        Assert.Contains("AfflictionJewelSmallPassivesGrantLife2_", pool);
        var plain = ClusterJewels.Create(large, null, 8, 2)!;
        Assert.Null(ClusterJewels.ThemeOf(large, plain));
        Assert.DoesNotContain("AfflictionNotablePrismaticHeart__", rolls.Pool(large, plain).Select(p => p.Mod.Id));

        // A small passive grants the theme's own stats and what the jewel's "also grant" adds.
        var conn = new ClientState(new(1, "cluster", 100, 100, 100, 100, null, []), 1, 1);
        InventoryItem[] placed = [new(11, 20, 0, jewel)];
        var layout = ClusterJewelLayout.For(20, jewel)!;
        int small = layout.ByOffset.First(p => p.Value == ClusterJewelLayout.Kind.Small).Key;
        conn.ClusterPassives.Add((ushort)(layout.Base + small));
        var granted = ClusterJewelStats.For(conn, placed, items.Mods, Program.Stats).ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(12, granted[Program.Stats.Key("fire_damage_+%")!.Value]);
        Assert.Equal(7, granted[Program.Stats.Key("base_maximum_life")!.Value]);
        // A jewel from before the server wrote themes names none: only its "also grant" counts, as it did.
        InventoryItem[] old = [new(12, 20, 0, plain with { Mods = [.. plain.Mods, new ItemMod(ModSection.Enchant, items.Mods.ById("AfflictionJewelSmallPassivesGrantLife_")!.Hash, [3])] })];
        var before = Assert.Single(ClusterJewelStats.For(conn, old, items.Mods, Program.Stats));
        Assert.Equal((Program.Stats.Key("base_maximum_life")!.Value, 3), before);

        // What a tool composes: a jewel that does not say its passives keeps a new one's enchantments; one that does is
        // taken as given. A unique has its own lists.
        var composed = ItemCrafter.Build(items, rolls, $$"""{"base":"{{large.Id}}","mods":[{"id":"AfflictionJewelSmallPassivesGrantLife2_"}]}""", 84, new Random(4), out string? error)!;
        Assert.Null(error);
        Assert.NotNull(ClusterJewels.ThemeOf(large, composed));
        Assert.InRange(Game.ClusterNodeBudget(composed), 8, 12);
        Assert.Equal([ModSection.Enchant, ModSection.Enchant, ModSection.Explicit], composed.Mods.Select(m => m.Section));
        var given = ItemCrafter.Build(items, rolls, $$"""{"base":"{{large.Id}}","mods":[{"id":"JewelExpansionPassiveNodes","section":"enchant","values":[13,10]}]}""", 84, new Random(4), out _)!;
        Assert.Equal((fire, 10, 1), (ClusterJewels.ThemeOf(large, given), Game.ClusterNodeBudget(given), given.Mods.Count));
        Assert.DoesNotContain(new UniqueCatalog(items).Create("Megalomaniac", new Random(202))!.Mods, m => m.Section == ModSection.Enchant);
    }
}

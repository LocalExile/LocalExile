using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TimelessJewelTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void The_rng_matches_the_reference_tinymt32_sequence()
    {
        var rng = new TinyMt32(26725, 2000);
        int[] expected = [73, 35, 97, 18, 24, 87, 56, 64];
        Assert.Equal(expected, Enumerable.Range(0, 8).Select(_ => (int)rng.Generate(0, 100)).ToArray());
    }

    [Fact]
    public void A_glorious_vanity_item_carries_the_alternate_tree_mod()
    {
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, 1);
        Assert.NotNull(jewel);
        Assert.Equal(ItemRarity.Unique, jewel.Rarity);
        Assert.Equal("Timeless Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Single(jewel.NameWords);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods);
        Assert.NotNull(spec);
        Assert.Equal(1, spec.TreeVersion);
        Assert.Equal(2000, spec.Seed);
        Assert.Equal(2000, spec.DisplayedSeed);
        Assert.Equal(1, spec.ConquerorIndex);
        Assert.Equal("Xibaqua", spec.ConquerorName);
        Assert.Equal(1500, spec.Radius);
        Assert.Single(jewel.Mods);
        Assert.Equal(6, jewel.Mods[0].Values.Count);
    }

    [Fact]
    public void Militant_Faith_carries_two_distinct_current_devotion_mods()
    {
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Militant Faith", 2000, 1, random: new Random(7));
        Assert.NotNull(jewel);
        Assert.Equal(3, jewel.Mods.Count);
        string[] devotion = [.. jewel.Mods.Skip(1).Select(m => Program.Items.Mods.ByHash(m.Hash)?.Id ?? "")];
        Assert.All(devotion, id => Assert.Contains("PerDevotion", id));
        Assert.Equal(2, devotion.Distinct().Count());
        Assert.DoesNotContain("ReducedManaCostPerDevotion", devotion);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods);
        Assert.NotNull(spec);
        Assert.Equal("Maxarius", spec.ConquerorName);
        Assert.Equal(2000, spec.Seed);
    }

    [Fact]
    public void A_legacy_revision_uses_the_legacy_conqueror_name_and_v0_keystone()
    {
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, 2);
        Assert.NotNull(jewel);
        var mod = jewel.Mods[0];
        jewel = jewel with { Mods = [mod with { Values = [.. mod.Values.Take(5), 0] }] };
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods);
        Assert.NotNull(spec);
        Assert.Equal("Zerphi", spec.ConquerorName);
        Assert.Equal(0, spec.ConquerorVersion);

        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var alternate = engine.Alternate(tree.Find(34098)!, spec, 34098);   // Mind Over Matter
        Assert.NotNull(alternate);
        Assert.Equal("Eternal Youth", alternate.Name);
    }

    [Fact]
    public void Elegant_Hubris_uses_the_displayed_x20_seed()
    {
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Elegant Hubris", 2000, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods);
        Assert.NotNull(spec);
        Assert.Equal(100, spec.Seed);
        Assert.Equal(2000, spec.DisplayedSeed);
        Assert.Null(TimelessJewelCatalog.Create(Program.Items, "Elegant Hubris", 2010, 1));
    }

    [Fact]
    public void The_graph_carries_the_client_tree_geometry()
    {
        var tree = new PassiveTree(Data());
        Assert.True(tree.HasGeometry);
        var socket = tree.JewelNodeForSlot(0);
        Assert.Equal((ushort)26725, socket);
        Assert.NotNull(tree.Position(26725));
        var nodes = tree.NodesInRadius(socket!.Value, 1500);
        Assert.Contains((ushort)62429, nodes);
        Assert.DoesNotContain((ushort)26725, nodes);
    }

    [Fact]
    public void A_vaal_small_passive_matches_the_pob_lookup_table()
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var node = tree.Find(62429);
        Assert.NotNull(node);
        var alternate = engine.Alternate(node, spec, 62429);
        Assert.NotNull(alternate);
        Assert.True(alternate.Replaces);
        Assert.Equal("Aura Effect", alternate.Name);
        // Path of Building's Glorious Vanity LUT, node 62429, seed 2000: vaal_small_aura_effect, value 3.
        Assert.Equal([(Program.Stats.Key("non_curse_aura_effect_+%")!.Value, 3)], alternate.Stats);
    }

    [Fact]
    public void A_vaal_notable_matches_the_pob_lookup_table()
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var node = tree.Find(37326);   // Stamina
        Assert.NotNull(node);
        var alternate = engine.Alternate(node, spec, 37326);
        Assert.NotNull(alternate);
        Assert.True(alternate.Replaces);
        Assert.Equal("Cult of Fire", alternate.Name);
        // Path of Building's LUT, node 37326, seed 2000: vaal_notable_fire_resistance_1, values 30 and 1.
        Assert.Equal([
            (Program.Stats.Key("base_fire_damage_resistance_%")!.Value, 30),
            (Program.Stats.Key("base_maximum_fire_damage_resistance_%")!.Value, 1),
        ], alternate.Stats);
    }

    [Theory]
    [InlineData(1, "Divine Flesh", "keystone_divine_flesh")]
    [InlineData(2, "Immortal Ambition", "keystone_soul_tether")]
    [InlineData(3, "Corrupted Soul", "keystone_corrupted_defences")]
    public void A_vaal_keystone_uses_the_selected_conqueror_version(int conqueror, string name, string stat)
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, conqueror);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var alternate = engine.Alternate(tree.Find(34098)!, spec, 34098);   // Mind Over Matter
        Assert.NotNull(alternate);
        Assert.True(alternate.Replaces);
        Assert.Equal(name, alternate.Name);
        Assert.Equal([(Program.Stats.Key(stat)!.Value, 1)], alternate.Stats);
    }

    [Fact]
    public void Only_one_Historic_timeless_jewel_can_be_socketed()
    {
        TestSaves.Ensure();
        string character = "TimelessLimit" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [26725, 46882];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 0, 0,
                TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, 1)!);
            var second = TimelessJewelCatalog.Create(Program.Items, "Lethal Pride", 12000, 1)!;
            Assert.Equal("only one Historic timeless jewel can be socketed",
                Program.PassiveJewelRefusal(conn, 12, 0, second));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_timeless_jewel_replaces_the_allocated_nodes_stats_in_the_pools()
    {
        TestSaves.Ensure();
        string character = "TimelessPool" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // Socket 0 (26725) and the Strength node in its radius; the jewel is in that socket's pane cell.
            conn.Passives = [26725, 62429];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 1);
            var jewel = TimelessJewelCatalog.Create(Program.Items, "Glorious Vanity", 2000, 1)!;
            items.Add(PlayerInventories.PassiveJewels, 0, 0, jewel);
            var with = Program.PoolsFor(conn, 1);
            Assert.Single(items.Items(PlayerInventories.PassiveJewels));
            Assert.Contains((ushort)62429, Program.Passives.NodesInRadius(26725, 1500));
            // The Strength small passive is replaced by Vaal Aura Effect: its 10 Strength (5 life) disappears.
            Assert.Equal(10, without.Strength - with.Strength);
            Assert.Equal(5, (int)(without.Life - with.Life));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Karui_adds_strength_to_an_attribute_small_passive()
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Lethal Pride", 12000, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var alternate = engine.Alternate(tree.Find(62429)!, spec, 62429);
        Assert.NotNull(alternate);
        Assert.False(alternate.Replaces);
        Assert.Equal([(Program.Stats.Key("base_strength")!.Value, 2)], alternate.Stats);
    }

    [Fact]
    public void Templar_replaces_an_attribute_small_with_devotion()
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Militant Faith", 2000, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var alternate = engine.Alternate(tree.Find(62429)!, spec, 62429);
        Assert.NotNull(alternate);
        Assert.True(alternate.Replaces);
        Assert.Equal("Devotion", alternate.Name);
        Assert.Equal([(Program.Stats.Key("base_devotion")!.Value, 10)], alternate.Stats);
    }

    [Fact]
    public void Kalguur_adds_ward_to_an_attribute_small()
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Heroic Tragedy", 100, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var alternate = engine.Alternate(tree.Find(62429)!, spec, 62429);
        Assert.NotNull(alternate);
        Assert.False(alternate.Replaces);
        Assert.Equal([(Program.Stats.Key("ward_+%")!.Value, 1)], alternate.Stats);
    }

    [Fact]
    public void Elegant_Hubris_blanks_a_small_passive()
    {
        var tree = new PassiveTree(Data());
        var engine = new TimelessJewelEngine(Data());
        var jewel = TimelessJewelCatalog.Create(Program.Items, "Elegant Hubris", 2000, 1);
        Assert.NotNull(jewel);
        var spec = TimelessJewelCatalog.Read(jewel, Program.Items.Mods)!;
        var alternate = engine.Alternate(tree.Find(62429)!, spec, 62429);
        Assert.NotNull(alternate);
        Assert.True(alternate.Replaces);
        Assert.Equal("Price of Glory", alternate.Name);
        Assert.Empty(alternate.Stats);
    }
}

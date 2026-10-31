using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RadiusJewelTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Theory]
    [InlineData("Brute Force Solution", "base_strength", "base_intelligence")]
    [InlineData("Careful Planning", "base_intelligence", "base_dexterity")]
    [InlineData("Efficient Training", "base_intelligence", "base_strength")]
    [InlineData("Fertile Mind", "base_dexterity", "base_intelligence")]
    [InlineData("Fluid Motion", "base_strength", "base_dexterity")]
    [InlineData("Inertia", "base_dexterity", "base_strength")]
    public void The_six_pilot_jewels_carry_the_expected_conversion(string name, string from, string to)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name);
        Assert.NotNull(jewel);
        Assert.Equal(2, jewel.Mods.Count);
        Assert.Single(jewel.NameWords);
        Assert.Equal(ItemRarity.Unique, jewel.Rarity);
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods);
        Assert.NotNull(conversion);
        Assert.Equal(Program.Stats.Key(from)!.Value, conversion.From);
        Assert.Equal(Program.Stats.Key(to)!.Value, conversion.To);
        Assert.Equal(1500, conversion.Radius);
    }

    [Fact]
    public void A_brute_force_solution_converts_strength_to_intelligence_in_the_pools()
    {
        TestSaves.Ensure();
        string character = "RadiusConversion" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // Socket 0 is 26725; 62429 is the 10-Strength small passive in its 1500 radius.
            conn.Passives = [26725, 62429];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 0, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Brute Force Solution")!);
            var with = Program.PoolsFor(conn, 20);

            // The node's 10 Strength becomes 10 Intelligence; the jewel also grants its max-rolled +24 Intelligence.
            Assert.Equal(without.Strength - 10, with.Strength);
            Assert.Equal(without.Intelligence + 34, with.Intelligence);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_radius_jewel_is_refused_in_a_cluster_socket()
    {
        TestSaves.Ensure();
        string character = "RadiusCluster" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [7960];   // large tree socket 11
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 11, 0, Program.CreateVoices(1)!);

            int child = Program.Passives.ClusterSlots(11)[0];
            var jewel = RadiusJewelCatalogue.Create(Program.Items, "Brute Force Solution")!;
            Assert.Equal("radius unique jewels go in an ordinary passive tree socket",
                Program.PassiveJewelRefusal(conn, child, 0, jewel));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData("Cold Steel", 1, "Viridian Jewel")]
    [InlineData("Fireborn", 1, "Crimson Jewel")]
    [InlineData("Energy From Within", 2, "Cobalt Jewel")]
    [InlineData("Healthy Mind", 2, "Cobalt Jewel")]
    [InlineData("Energised Armour", 2, "Crimson Jewel")]
    public void The_damage_and_defence_conversion_jewels_have_the_expected_item(string name, int mods, string baseName)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name);
        Assert.NotNull(jewel);
        Assert.Equal(mods, jewel.Mods.Count);
        Assert.Equal(baseName, Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Single(jewel.NameWords);
        Assert.NotNull(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
    }

    [Fact]
    public void Cold_steel_swaps_physical_and_cold_damage()
    {
        uint physical = Program.Stats.Key("physical_damage_+%")!.Value;
        uint cold = Program.Stats.Key("cold_damage_+%")!.Value;
        uint life = Program.Stats.Key("maximum_life_+%")!.Value;
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Cold Steel")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.NotNull(conversion.Swap);
        Assert.Equal(1500, conversion.Radius);

        var stats = RadiusJewelCatalogue.Convert(
            [(physical, 20), (cold, 10), (life, 5)], conversion);

        Assert.Equal(10, stats.Single(stat => stat.Key == physical).Value);
        Assert.Equal(20, stats.Single(stat => stat.Key == cold).Value);
        Assert.Equal(5, stats.Single(stat => stat.Key == life).Value);
    }

    [Fact]
    public void Fireborn_moves_the_other_damage_types_to_fire()
    {
        uint physical = Program.Stats.Key("physical_damage_+%")!.Value;
        uint cold = Program.Stats.Key("cold_damage_+%")!.Value;
        uint lightning = Program.Stats.Key("lightning_damage_+%")!.Value;
        uint chaos = Program.Stats.Key("chaos_damage_+%")!.Value;
        uint elemental = Program.Stats.Key("elemental_damage_+%")!.Value;
        uint fire = Program.Stats.Key("fire_damage_+%")!.Value;
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Fireborn")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Null(conversion.Swap);
        Assert.Equal(1200, conversion.Radius);

        var stats = RadiusJewelCatalogue.Convert(
            [(physical, 10), (cold, 20), (lightning, 30), (chaos, 40), (elemental, 50), (fire, 5)], conversion);

        Assert.Equal(155, stats.Single(stat => stat.Key == fire).Value);
        Assert.DoesNotContain(stats, stat => stat.Key == physical || stat.Key == cold || stat.Key == lightning
            || stat.Key == chaos || stat.Key == elemental);
    }

    [Fact]
    public void Energy_from_within_moves_life_percent_to_energy_shield()
    {
        uint life = Program.Stats.Key("maximum_life_+%")!.Value;
        uint energyShield = Program.Stats.Key("maximum_energy_shield_+%")!.Value;
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Energy From Within")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;

        var move = Assert.Single(conversion.Moves);
        Assert.Equal(life, move.From);
        Assert.Equal(energyShield, move.To);
        Assert.Equal(1, move.Factor);
        Assert.Equal(1500, conversion.Radius);
        Assert.Equal(20, RadiusJewelCatalogue.Convert([(life, 20)], conversion).Single(stat => stat.Key == energyShield).Value);
    }

    [Fact]
    public void Healthy_mind_doubles_life_percent_into_mana_percent()
    {
        uint life = Program.Stats.Key("maximum_life_+%")!.Value;
        uint mana = Program.Stats.Key("maximum_mana_+%")!.Value;
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Healthy Mind")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;

        var move = Assert.Single(conversion.Moves);
        Assert.Equal(life, move.From);
        Assert.Equal(mana, move.To);
        Assert.Equal(2, move.Factor);
        Assert.Equal(1500, conversion.Radius);
        Assert.Equal(40, RadiusJewelCatalogue.Convert([(life, 20)], conversion).Single(stat => stat.Key == mana).Value);
    }

    [Fact]
    public void Energised_armour_doubles_energy_shield_percent_into_armour_percent()
    {
        uint energyShield = Program.Stats.Key("maximum_energy_shield_+%")!.Value;
        uint armour = Program.Stats.Key("physical_damage_reduction_rating_+%")!.Value;
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Energised Armour")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;

        var move = Assert.Single(conversion.Moves);
        Assert.Equal(energyShield, move.From);
        Assert.Equal(armour, move.To);
        Assert.Equal(2, move.Factor);
        Assert.Equal(1500, conversion.Radius);
        Assert.Equal(40, RadiusJewelCatalogue.Convert([(energyShield, 20)], conversion)
            .Single(stat => stat.Key == armour).Value);
    }

    [Fact]
    public void Healthy_mind_has_a_one_item_limit()
    {
        TestSaves.Ensure();
        string character = "RadiusLimit" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [26725, 36634];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 0, 0, RadiusJewelCatalogue.Create(Program.Items, "Healthy Mind")!);
            var second = RadiusJewelCatalogue.Create(Program.Items, "Healthy Mind")!;
            Assert.Equal("only 1 Healthy Mind can be socketed",
                Program.PassiveJewelRefusal(conn, 1, 0, second));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Energy_from_within_conversion_reaches_the_life_pool()
    {
        TestSaves.Ensure();
        string character = "RadiusLifePool" + Guid.NewGuid().ToString("N");
        try
        {
            var tree = new PassiveTree(Data());
            uint life = Program.Stats.Key("maximum_life_+%")!.Value;
            var node = tree.NodesInRadius(26725, 1500)
                .Select(tree.Find)
                .First(n => n is { IsAscendancy: false, IsStart: false, IsJewelSocket: false, IsMastery: false,
                    IsProxy: false, IsExpansion: false } && n.Stats.Any(stat => stat.Key == life))!;

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [26725, node.GraphId];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 0, 0, RadiusJewelCatalogue.Create(Program.Items, "Energy From Within")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.True(with.Life < without.Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData("Fevered Mind", 2, "Cobalt Jewel", 1)]
    [InlineData("Fortress Covenant", 4, "Cobalt Jewel", 20)]
    [InlineData("Quickening Covenant", 3, "Viridian Jewel", 20)]
    public void The_notable_replacement_jewels_have_the_expected_item(string name, int mods, string baseName, byte level)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name);
        Assert.NotNull(jewel);
        Assert.Equal(mods, jewel.Mods.Count);
        Assert.Equal(baseName, Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Equal(level, jewel.ItemLevel);
        Assert.True(jewel.Corrupted);
        Assert.Single(jewel.NameWords);
        Assert.NotNull(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
    }

    [Fact]
    public void Fevered_mind_replaces_notables_with_mana_cost_and_spell_damage()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Fevered Mind")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Equal(800, conversion.Radius);
        Assert.NotNull(conversion.NotableStats);
        Assert.Equal(10, conversion.NotableStats.Single(stat => stat.Key == Program.Stats.Key("mana_cost_+%")!.Value).Value);
        Assert.Equal(20, conversion.NotableStats.Single(stat => stat.Key == Program.Stats.Key("spell_damage_+%")!.Value).Value);
    }

    [Fact]
    public void Fortress_covenant_replaces_notables_with_minion_damage_taken()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Fortress Covenant")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Equal(1200, conversion.Radius);
        var stat = Assert.Single(conversion.NotableStats!);
        Assert.Equal(Program.Stats.Key("minion_damage_taken_+%")!.Value, stat.Key);
        Assert.Equal(20, stat.Value);
    }

    [Fact]
    public void Quickening_covenant_replaces_notables_with_reduced_minion_movement_speed()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Quickening Covenant")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Equal(1200, conversion.Radius);
        var stat = Assert.Single(conversion.NotableStats!);
        Assert.Equal(Program.Stats.Key("minion_movement_speed_+%")!.Value, stat.Key);
        Assert.Equal(-25, stat.Value);
    }

    [Fact]
    public void Fevered_mind_replacement_removes_the_allocated_notable_stats()
    {
        TestSaves.Ensure();
        string character = "RadiusNotable" + Guid.NewGuid().ToString("N");
        try
        {
            var tree = new PassiveTree(Data());
            uint life = Program.Stats.Key("maximum_life_+%")!.Value;
            var notable = tree.NodesInRadius(26725, 800)
                .Select(tree.Find)
                .First(n => n is { IsNotable: true } && n.Stats.Any(stat => stat.Key == life))!;

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [26725, notable.GraphId];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 0, 0, RadiusJewelCatalogue.Create(Program.Items, "Fevered Mind")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.True(with.Life < without.Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData("Coated Shrapnel", 2, "Crimson Jewel", 0)]
    [InlineData("Martial Artistry", 2, "Crimson Jewel", 1)]
    [InlineData("The Blue Dream", 2, "Cobalt Jewel", 0)]
    [InlineData("The Blue Nightmare", 3, "Cobalt Jewel", 1)]
    [InlineData("The Green Dream", 2, "Viridian Jewel", 0)]
    [InlineData("The Green Nightmare", 3, "Viridian Jewel", 1)]
    [InlineData("The Red Dream", 2, "Crimson Jewel", 0)]
    [InlineData("The Red Nightmare", 3, "Crimson Jewel", 1)]
    [InlineData("The Light of Meaning", 2, "Prismatic Jewel", 1)]
    [InlineData("The Perandus Pact", 2, "Prismatic Jewel", 1)]
    public void The_passive_addition_jewels_have_the_expected_item(string name, int mods, string baseName, int limit)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name);
        Assert.NotNull(jewel);
        Assert.Equal(mods, jewel.Mods.Count);
        Assert.Equal(baseName, Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Single(jewel.NameWords);
        var entry = RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods);
        Assert.NotNull(entry);
        Assert.Equal(limit, entry.Limit);
        Assert.NotNull(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
    }

    [Fact]
    public void Coated_shrapnel_grants_trap_and_mine_physical_damage_to_passives()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Coated Shrapnel")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Equal(800, conversion.Radius);
        Assert.NotNull(conversion.AdditionStats);
        Assert.Equal(3, conversion.AdditionStats.Single(stat =>
            stat.Key == Program.Stats.Key("trap_and_mine_minimum_added_physical_damage")!.Value).Value);
        Assert.Equal(6, conversion.AdditionStats.Single(stat =>
            stat.Key == Program.Stats.Key("trap_and_mine_maximum_added_physical_damage")!.Value).Value);
    }

    [Fact]
    public void Martial_artistry_grants_unarmed_melee_attack_speed()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Martial Artistry")!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Equal(800, conversion.Radius);
        var stat = Assert.Single(conversion.AdditionStats!);
        Assert.Equal(Program.Stats.Key("unarmed_melee_attack_speed_+%")!.Value, stat.Key);
        Assert.Equal(1, stat.Value);
    }

    [Theory]
    [InlineData("The Blue Dream", "base_lightning_damage_resistance_%", "add_power_charge_on_kill_%_chance", 100, 40)]
    [InlineData("The Blue Nightmare", "base_lightning_damage_resistance_%", "additional_spell_block_%", 50, 20)]
    [InlineData("The Green Dream", "base_cold_damage_resistance_%", "add_frenzy_charge_on_kill_%_chance", 100, 40)]
    [InlineData("The Green Nightmare", "base_cold_damage_resistance_%", "base_spell_suppression_chance_%", 70, 28)]
    [InlineData("The Red Dream", "base_fire_damage_resistance_%", "endurance_charge_on_kill_%", 100, 40)]
    [InlineData("The Red Nightmare", "base_fire_damage_resistance_%", "additional_block_%", 50, 20)]
    public void The_breach_jewels_scale_the_matching_resistance(
        string name, string source, string target, int scale, int expected)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name)!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.Equal(1500, conversion.Radius);
        Assert.NotNull(conversion.Resistance);
        Assert.Equal(Program.Stats.Key(target)!.Value, conversion.Resistance.Target);
        Assert.Equal(scale, conversion.Resistance.ScalePercent);
        Assert.Equal(expected, conversion.Resistance.Added([(Program.Stats.Key(source)!.Value, 40)]));
    }

    [Fact]
    public void Light_of_meaning_selects_one_of_its_variants()
    {
        var life = RadiusJewelCatalogue.Create(Program.Items, "The Light of Meaning", variant: "Life")!;
        var lifeConversion = RadiusJewelCatalogue.ReadConversion(life, Program.Items.Mods)!;
        Assert.Equal(1500, lifeConversion.Radius);
        Assert.Equal(5, lifeConversion.AdditionStats!.Single(stat =>
            stat.Key == Program.Stats.Key("base_maximum_life")!.Value).Value);

        var fire = RadiusJewelCatalogue.Create(Program.Items, "The Light of Meaning", variant: "Fire Damage")!;
        var fireConversion = RadiusJewelCatalogue.ReadConversion(fire, Program.Items.Mods)!;
        Assert.Equal(6, fireConversion.AdditionStats!.Single(stat =>
            stat.Key == Program.Stats.Key("fire_damage_+%")!.Value).Value);

        Assert.NotNull(RadiusJewelCatalogue.FindWithVariant("The Light of Meaning Fire Damage"));
        Assert.Null(RadiusJewelCatalogue.Create(Program.Items, "The Light of Meaning", variant: "Not a Variant"));
    }

    [Fact]
    public void Light_of_meaning_life_addition_reaches_the_life_pool()
    {
        TestSaves.Ensure();
        string character = "RadiusAddition" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [26725, 62429];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 0, 0,
                RadiusJewelCatalogue.Create(Program.Items, "The Light of Meaning", variant: "Life")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Life + 5, with.Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData("Anatomical Knowledge", 2, "Cobalt Jewel", 0)]
    [InlineData("Eldritch Knowledge", 1, "Cobalt Jewel", 0)]
    [InlineData("Pugilist", 1, "Viridian Jewel", 0)]
    [InlineData("Spire of Stone", 2, "Crimson Jewel", 1)]
    [InlineData("Static Electricity", 1, "Viridian Jewel", 0)]
    [InlineData("Tempered Flesh", 2, "Crimson Jewel", 0)]
    [InlineData("Tempered Mind", 2, "Cobalt Jewel", 0)]
    [InlineData("Tempered Spirit", 2, "Viridian Jewel", 0)]
    [InlineData("Transcendent Flesh", 4, "Crimson Jewel", 0)]
    [InlineData("Transcendent Mind", 4, "Cobalt Jewel", 0)]
    [InlineData("Transcendent Spirit", 4, "Viridian Jewel", 0)]
    public void The_per_node_scaling_jewels_have_the_expected_item(string name, int mods, string baseName, int limit)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name);
        Assert.NotNull(jewel);
        Assert.Equal(mods, jewel.Mods.Count);
        Assert.Equal(baseName, Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Single(jewel.NameWords);
        var entry = RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods);
        Assert.NotNull(entry);
        Assert.Equal(limit, entry.Limit);
        Assert.NotNull(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
    }

    [Theory]
    [InlineData("Anatomical Knowledge", "base_maximum_life", 0, 3, true)]
    [InlineData("Eldritch Knowledge", "chaos_damage_+%", 10, 5, false)]
    [InlineData("Spire of Stone", "totem_life_+%", 10, 3, false)]
    [InlineData("Static Electricity", "attack_maximum_added_lightning_damage", 0, 1, true)]
    [InlineData("Tempered Flesh", "base_strength", 1, -1, false)]
    [InlineData("Transcendent Flesh", "base_strength", 1, -1, false)]
    public void The_per_node_scaling_primary_rule_is_read(string name, string target, int per, int value, bool divisor)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name)!;
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!;
        Assert.NotNull(conversion.Scaling);
        var stat = conversion.Scaling.First(s => s.Target == Program.Stats.Key(target)!.Value);
        Assert.Equal(per, stat.Per);
        Assert.Equal(value, stat.Value);
        Assert.Equal(divisor, stat.ValueIsDivisor);
    }

    [Fact]
    public void Pugilist_and_Tempered_jewels_have_the_expected_scaling_count()
    {
        Assert.Equal(3, RadiusJewelCatalogue.ReadConversion(
            RadiusJewelCatalogue.Create(Program.Items, "Pugilist")!, Program.Items.Mods)!.Scaling!.Count);
        Assert.Equal(2, RadiusJewelCatalogue.ReadConversion(
            RadiusJewelCatalogue.Create(Program.Items, "Tempered Flesh")!, Program.Items.Mods)!.Scaling!.Count);
        Assert.Equal(4, RadiusJewelCatalogue.ReadConversion(
            RadiusJewelCatalogue.Create(Program.Items, "Transcendent Flesh")!, Program.Items.Mods)!.Scaling!.Count);
    }

    [Fact]
    public void Tempered_flesh_subtracts_the_allocated_strength()
    {
        TestSaves.Ensure();
        string character = "RadiusScaling" + Guid.NewGuid().ToString("N");
        try
        {
            var tree = new PassiveTree(Data());
            uint strength = Program.Stats.Key("base_strength")!.Value;
            var node = tree.NodesInRadius(26725, 1200)
                .Select(tree.Find)
                .First(n => n is { IsAscendancy: false, IsStart: false, IsJewelSocket: false, IsMastery: false,
                    IsProxy: false, IsExpansion: false } && n.Stats.Any(stat => stat.Key == strength))!;
            int allocatedStrength = node.Stats.Where(stat => stat.Key == strength).Sum(stat => stat.Value);

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [26725, node.GraphId];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 0, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Tempered Flesh")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Strength - allocatedStrength, with.Strength);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Might_of_the_Meek_has_the_expected_item()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Might of the Meek");
        Assert.NotNull(jewel);
        Assert.Single(jewel.Mods);
        Assert.Equal("Crimson Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Equal(20, jewel.ItemLevel);
        var entry = RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Limit);

        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods);
        Assert.NotNull(conversion);
        Assert.Equal(1500, conversion.Radius);
        Assert.Equal(50, conversion.EffectPct);
        Assert.True(conversion.SuppressNotables);
        Assert.Null(conversion.Scaling);
        Assert.Empty(conversion.Moves);
        Assert.Null(conversion.Swap);
    }

    [Fact]
    public void Might_in_All_Forms_has_the_expected_item()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Might in All Forms");
        Assert.NotNull(jewel);
        Assert.Single(jewel.Mods);
        Assert.Equal("Crimson Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        var entry = RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Limit);

        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods);
        Assert.NotNull(conversion);
        Assert.Equal(1200, conversion.Radius);
        Assert.True(conversion.StrengthMeleeBonus);
        Assert.Empty(conversion.Moves);
        Assert.Null(conversion.Swap);
        uint dexterity = Program.Stats.Key("base_dexterity")!.Value;
        uint intelligence = Program.Stats.Key("base_intelligence")!.Value;
        uint life = Program.Stats.Key("maximum_life_+%")!.Value;
        Assert.Equal(10, RadiusJewelCatalogue.MeleeAttributes(
            [(dexterity, 6), (intelligence, 4), (life, 50)]));
    }

    [Theory]
    [InlineData("Pitch Darkness")]
    [InlineData("Sudden Ignition")]
    public void Global_only_radius_jewels_have_an_item_but_no_radius_conversion(string name)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, name);
        Assert.NotNull(jewel);
        Assert.Equal(3, jewel.Mods.Count);
        Assert.NotNull(RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods));
        Assert.Null(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
        var radius = jewel.Mods.Single(mod =>
            Program.Items.Mods.ByHash(mod.Hash)?.Id == "JewelEffectBaseRadiusUnique__1");
        Assert.Equal(1200, radius.Values[0]);
    }

    [Fact]
    public void Every_threshold_jewel_has_a_creatable_item()
    {
        Assert.Equal(33, ThresholdJewelCatalogue.All.Length);
        foreach (var entry in ThresholdJewelCatalogue.All)
        {
            var jewel = RadiusJewelCatalogue.Create(Program.Items, entry);
            Assert.NotNull(jewel);
            Assert.NotEmpty(jewel.Mods);
            Assert.NotNull(RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods));
            Assert.Null(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
        }
    }

    [Fact]
    public void Combat_Focus_can_be_selected_by_base()
    {
        var cobalt = RadiusJewelCatalogue.Find("Combat Focus Cobalt");
        Assert.NotNull(cobalt);
        Assert.Equal("Cobalt Jewel", cobalt.Base);
        var jewel = RadiusJewelCatalogue.Create(Program.Items, cobalt);
        Assert.NotNull(jewel);
        Assert.Equal("Cobalt Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
    }

    [Fact]
    public void Impossible_Escape_has_a_keystone_variant_item()
    {
        Assert.Equal(48, ImpossibleEscapeCatalogue.All.Count);
        Assert.Equal((ushort)50679, Program.Passives.GraphIdForRow(2444));
        var keystone = ImpossibleEscapeCatalogue.Find("Versatile Combatant");
        Assert.NotNull(keystone);
        Assert.Equal(2444, keystone.Row);
        Assert.Equal((ushort)50679, keystone.GraphId);

        var jewel = ImpossibleEscapeCatalogue.Create(Program.Items, "Versatile Combatant");
        Assert.NotNull(jewel);
        Assert.True(jewel!.Corrupted);
        Assert.Single(jewel.NameWords);
        Assert.Equal("Viridian Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        Assert.Equal(50679, jewel.Mods.Single().Values[0]);
        var read = ImpossibleEscapeCatalogue.Read(jewel, Program.Items.Mods);
        Assert.NotNull(read);
        Assert.Equal((50679, 960), read.Value);
    }

    [Fact]
    public void Impossible_Escape_migrates_a_legacy_row_value_to_the_keystone_hash()
    {
        var item = ImpossibleEscapeCatalogue.Create(Program.Items, "Supreme Ego")!;
        var mod = item.Mods.Single();
        var legacy = item with { Mods = [mod with { Values = [2312, 800] }] };
        var migrated = ImpossibleEscapeCatalogue.Migrate(legacy, Program.Items.Mods);
        Assert.Equal(49639, migrated.Mods.Single().Values[0]);
        Assert.Equal((49639, 960), ImpossibleEscapeCatalogue.Read(migrated, Program.Items.Mods));
    }

    [Fact]
    public void Only_one_Impossible_Escape_can_be_socketed()
    {
        TestSaves.Ensure();
        string character = "ImpossibleLimit" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(5)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 4, 0,
                ImpossibleEscapeCatalogue.Create(Program.Items, "Versatile Combatant")!);
            Assert.Equal("only 1 Impossible Escape can be socketed", Program.PassiveJewelRefusal(conn, 5, 0,
                ImpossibleEscapeCatalogue.Create(Program.Items, "Minion Instability")!));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Threshold_jewel_mods_parse_into_attribute_conditions()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Collateral Damage")!;
        var (radius, effects) = ThresholdJewels.Read(jewel, Program.Items.Mods);
        Assert.Equal(1200, radius);

        var area = effects.Single(effect =>
            effect.Key == Program.Stats.Key("galvanic_arrow_area_damage_+%"));
        Assert.Equal(ThresholdJewels.Attributes.Dexterity, area.Attributes);
        Assert.Equal(40, area.Required);
        Assert.Equal(50, area.Value);

        var shrapnel = effects.Single(effect =>
            effect.Key == Program.Stats.Key("shrapnel_shot_radius_+%"));
        Assert.Equal(ThresholdJewels.Attributes.Dexterity, shrapnel.Attributes);
        Assert.Equal(50, shrapnel.Required);
        Assert.Equal(25, shrapnel.Value);
    }

    [Fact]
    public void Threshold_aliases_use_the_client_stat_targets()
    {
        var vigil = RadiusJewelCatalogue.Create(Program.Items, "The Vigil")!;
        var (_, vigilEffects) = ThresholdJewels.Read(vigil, Program.Items.Mods);
        Assert.True(vigilEffects.Count > 0,
            string.Join(",", vigil.Mods.Select(mod => Program.Items.Mods.ByHash(mod.Hash)?.Id)));
        Assert.Contains(vigilEffects, effect =>
            effect.Key == Program.Stats.Key("vigilant_strike_applies_to_nearby_allies_for_X_seconds") &&
            effect.Value == 8);

        var trail = RadiusJewelCatalogue.Create(Program.Items, "Frozen Trail")!;
        var (_, trailEffects) = ThresholdJewels.Read(trail, Program.Items.Mods);
        Assert.True(trailEffects.Count > 0,
            string.Join(",", trail.Mods.Select(mod => Program.Items.Mods.ByHash(mod.Hash)?.Id)));
        Assert.Contains(trailEffects, effect =>
            effect.Key == Program.Stats.Key("frostbolt_number_of_additional_projectiles") &&
            effect.Value == 2);
    }

    [Fact]
    public void Threshold_jewels_apply_only_when_the_radius_attribute_total_is_met()
    {
        TestSaves.Ensure();
        string character = "RadiusThreshold" + Guid.NewGuid().ToString("N");
        try
        {
            // Socket 36634 has 60 Int / 10 Str / 0 Dex in radius; 60735 has 60 Dex; 41263 has 30 Int.
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [36634, 60735, 41263];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 1, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Combat Focus Cobalt")!);
            items.Add(PlayerInventories.PassiveJewels, 4, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Collateral Damage")!);
            items.Add(PlayerInventories.PassiveJewels, 3, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Spirited Response")!);

            Program.PoolsFor(conn, 20);
            var sources = conn.StatSourcesCache!.Sources;
            Assert.Equal(1, sources.GetValueOrDefault(
                Program.Stats.Key("elemental_hit_cannot_roll_fire_damage")!.Value));
            Assert.Equal(50, sources.GetValueOrDefault(
                Program.Stats.Key("galvanic_arrow_area_damage_+%")!.Value));
            Assert.Equal(25, sources.GetValueOrDefault(
                Program.Stats.Key("shrapnel_shot_radius_+%")!.Value));
            // 30 Int is below Spirited Response's 40-Int requirement, so its recoup stat stays absent.
            Assert.Equal(0, sources.GetValueOrDefault(
                Program.Stats.Key("warcry_damage_taken_goes_to_mana_%")!.Value));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Lioneyes_Fall_has_an_item_source_but_no_server_conversion()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Lioneye's Fall");
        Assert.NotNull(jewel);
        Assert.Single(jewel.Mods);
        Assert.NotNull(RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods));
        Assert.Null(RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods));
    }

    [Fact]
    public void Might_in_All_Forms_counts_radius_dex_and_int_toward_the_melee_bonus()
    {
        TestSaves.Ensure();
        string character = "RadiusAllForms" + Guid.NewGuid().ToString("N");
        try
        {
            // Socket 36634 (cell 1) and the Intelligence small 7388 at 229 units in its 1200 radius.
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [36634, 7388];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 1, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Might in All Forms")!);
            var with = Program.PoolsFor(conn, 20);

            // 10 Intelligence in radius = 2% increased melee physical damage (10 / 5).
            Assert.Equal(without.MeleePhysicalDamagePct + 2, with.MeleePhysicalDamagePct);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData(10, 50, 15)]
    [InlineData(5, 50, 7)]
    [InlineData(-5, 50, -7)]
    [InlineData(10, 100, 20)]
    [InlineData(1, 50, 1)]
    public void Might_of_the_Meek_effect_rounds_toward_zero(int value, int pct, int expected)
    {
        Assert.Equal(expected, RadiusJewelCatalogue.ScaleEffect(value, pct));
    }

    [Fact]
    public void Might_of_the_Meek_scales_non_keystone_passives()
    {
        TestSaves.Ensure();
        string character = "RadiusEffect" + Guid.NewGuid().ToString("N");
        try
        {
            var tree = new PassiveTree(Data());
            const ushort socket = 28475; // pane cell 7
            uint strength = Program.Stats.Key("base_strength")!.Value;
            var node = tree.NodesInRadius(socket, 1500)
                .Select(tree.Find)
                .First(n => n is { IsNotable: false, IsKeystone: false, IsAscendancy: false, IsStart: false,
                    IsJewelSocket: false, IsMastery: false, IsProxy: false, IsExpansion: false }
                    && n.Stats.Any(stat => stat.Key == strength && stat.Value == 10))!;

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [socket, node.GraphId];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 7, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Might of the Meek")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Strength + 5, with.Strength);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Might_of_the_Meek_suppresses_notables()
    {
        TestSaves.Ensure();
        string character = "RadiusSuppress" + Guid.NewGuid().ToString("N");
        try
        {
            var tree = new PassiveTree(Data());
            const ushort socket = 31683; // pane cell 6
            uint strength = Program.Stats.Key("base_strength")!.Value;
            var node = tree.NodesInRadius(socket, 1500)
                .Select(tree.Find)
                .First(n => n is { IsNotable: true, IsKeystone: false, IsAscendancy: false, IsStart: false,
                    IsJewelSocket: false, IsMastery: false, IsProxy: false, IsExpansion: false }
                    && n.Stats.Any(stat => stat.Key == strength))!;
            int notableStrength = node.Stats.Where(stat => stat.Key == strength).Sum(stat => stat.Value);

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [socket, node.GraphId];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 6, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Might of the Meek")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Strength - notableStrength, with.Strength);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Intuitive_Leap_has_the_allocation_rule()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Intuitive Leap");
        Assert.NotNull(jewel);
        Assert.Single(jewel.Mods);
        Assert.Equal("Viridian Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        var entry = RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Limit);

        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods);
        Assert.NotNull(conversion);
        Assert.Equal(800, conversion.Radius);
        Assert.True(conversion.AllocateDisconnected);
        Assert.Equal(0, conversion.Ring);
    }

    [Theory]
    [InlineData("Small Ring", 1)]
    [InlineData("Medium Ring", 2)]
    [InlineData("Large Ring", 3)]
    [InlineData("Very Large Ring", 4)]
    [InlineData("Massive Ring", 5)]
    public void Thread_of_Hope_selects_the_ring_variant(string variant, int ring)
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Thread of Hope", variant: variant);
        Assert.NotNull(jewel);
        Assert.Equal(3, jewel.Mods.Count);
        Assert.Equal("Crimson Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        var ids = jewel.Mods.Select(mod => Program.Items.Mods.ByHash(mod.Hash)?.Id).ToArray();
        Assert.StartsWith("JewelRingRadiusValues", ids[0]);
        Assert.Equal(ModSection.Implicit, jewel.Mods[0].Section);
        Assert.Equal("JewelUniqueAllocateDisconnectedPassives", ids[1]);
        Assert.Equal("AllResistancesUnique__6", ids[2]);
        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods);
        Assert.NotNull(conversion);
        Assert.Equal(800, conversion.Radius);
        Assert.True(conversion.AllocateDisconnected);
        Assert.Equal(ring, conversion.Ring);

        var (row, mod) = jewel.Mods
            .Select(mod => (Row: Program.Items.Mods.ByHash(mod.Hash), Mod: mod))
            .First(x => x.Row?.Id.StartsWith("JewelRingRadiusValues", StringComparison.Ordinal) == true)!;
        int index = row!.Stats.ToList().FindIndex(stat => stat.Id == "local_jewel_variable_ring_radius_value");
        Assert.True(index >= 0);
        Assert.Equal(ring, mod.Values[index]);
    }

    [Fact]
    public void Thread_of_Hope_does_not_bypass_jewel_sockets()
    {
        var tree = new PassiveTree(Data());
        var socket = tree.Find(26725);
        Assert.NotNull(socket);
        Assert.True(socket.IsJewelSocket);

        var thread = RadiusJewelCatalogue.ReadConversion(
            RadiusJewelCatalogue.Create(Program.Items, "Thread of Hope", variant: "Small Ring")!, Program.Items.Mods)!;
        var leap = RadiusJewelCatalogue.ReadConversion(
            RadiusJewelCatalogue.Create(Program.Items, "Intuitive Leap")!, Program.Items.Mods)!;
        Assert.False(RadiusJewelCatalogue.BypassIncludes(thread, socket));
        Assert.True(RadiusJewelCatalogue.BypassIncludes(leap, socket));
    }

    [Fact]
    public void Unnatural_Instinct_has_the_small_swap()
    {
        var jewel = RadiusJewelCatalogue.Create(Program.Items, "Unnatural Instinct");
        Assert.NotNull(jewel);
        Assert.Equal(2, jewel.Mods.Count);
        Assert.Equal("Viridian Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
        var entry = RadiusJewelCatalogue.EntryOf(jewel, Program.Items.Mods);
        Assert.NotNull(entry);
        Assert.Equal(1, entry.Limit);

        var conversion = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods);
        Assert.NotNull(conversion);
        Assert.Equal(800, conversion.Radius);
        Assert.True(conversion.SuppressAllocatedSmalls);
        Assert.True(conversion.CopyUnallocatedSmalls);
    }

    [Fact]
    public void Ring_radii_match_the_pob_3_16_variable_rings()
    {
        Assert.Equal((960, 1320), RadiusJewelCatalogue.RingRadii(1));
        Assert.Equal((1320, 1680), RadiusJewelCatalogue.RingRadii(2));
        Assert.Equal((1680, 2040), RadiusJewelCatalogue.RingRadii(3));
        Assert.Equal((2040, 2400), RadiusJewelCatalogue.RingRadii(4));
        Assert.Equal((2400, 2880), RadiusJewelCatalogue.RingRadii(5));
        Assert.Equal((0, 0), RadiusJewelCatalogue.RingRadii(0));
    }

    [Fact]
    public void Nodes_in_ring_are_between_the_client_radii()
    {
        var tree = new PassiveTree(Data());
        var ring = tree.NodesInRing(26725, 800, 1100);
        Assert.NotEmpty(ring);
        foreach (ushort node in ring)
        {
            double distanceSquared = tree.DistanceSquared(26725, node)!.Value;
            Assert.True(distanceSquared >= 800 * 800);
            Assert.True(distanceSquared <= 1100 * 1100);
        }
    }

    [Fact]
    public void Unnatural_Instinct_removes_allocated_smalls_and_grants_unallocated_ones()
    {
        TestSaves.Ensure();
        string character = "RadiusSmallSwap" + Guid.NewGuid().ToString("N");
        try
        {
            var tree = new PassiveTree(Data());
            const ushort socket = 34483; // pane cell 10
            uint intelligence = Program.Stats.Key("base_intelligence")!.Value;
            uint dexterity = Program.Stats.Key("base_dexterity")!.Value;
            var allocated = tree.NodesInRadius(socket, 800)
                .Select(tree.Find)
                .First(n => n is { IsNotable: false, IsKeystone: false, IsAscendancy: false, IsStart: false,
                    IsJewelSocket: false, IsMastery: false, IsProxy: false, IsExpansion: false }
                    && n.Stats.Any(stat => stat.Key == intelligence && stat.Value == 10))!;
            var unallocated = tree.NodesInRadius(socket, 800)
                .Select(tree.Find)
                .Where(n => n!.GraphId != allocated.GraphId
                    && n is { IsNotable: false, IsKeystone: false, IsAscendancy: false, IsStart: false,
                        IsJewelSocket: false, IsMastery: false, IsProxy: false, IsExpansion: false })
                .ToList();
            int copiedIntelligence = unallocated.Sum(n => n!.Stats
                .Where(stat => stat.Key == intelligence).Sum(stat => stat.Value));
            int copiedDexterity = unallocated.Sum(n => n!.Stats
                .Where(stat => stat.Key == dexterity).Sum(stat => stat.Value));
            Assert.True(copiedDexterity > 0);

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [socket, allocated.GraphId];
            var items = PlayerInventories.For(character, Program.Items);
            var without = Program.PoolsFor(conn, 20);

            items.Add(PlayerInventories.PassiveJewels, 10, 0,
                RadiusJewelCatalogue.Create(Program.Items, "Unnatural Instinct")!);
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Intelligence - 10 + copiedIntelligence, with.Intelligence);
            Assert.Equal(without.Dexterity + copiedDexterity, with.Dexterity);
        }
        finally { PlayerInventories.Forget(character); }
    }
}

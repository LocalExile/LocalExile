namespace GameServer.Gateway.Tests;

public class TattooTests
{
    [Fact]
    public void The_catalogue_has_all_100_tattoos_and_the_expected_targets()
    {
        Assert.Equal(100, TattooCatalogue.All.Count);
        var fire = TattooCatalogue.Find("Tattoo of the Ngamahu Firewalker");
        Assert.NotNull(fire);
        Assert.Equal("Small Strength", fire.Target);
        Assert.Equal(0, fire.Override);
        Assert.Equal(0x5C2A, fire.OverrideHash);
        Assert.False(fire.Forbidden);
        Assert.False(fire.EventOnly);

        var forbidden = TattooCatalogue.Find("Forbidden Tattoo of the Ranger");
        Assert.NotNull(forbidden);
        Assert.Equal("Ascendancy Notable", forbidden.Target);
        Assert.True(forbidden.Forbidden);
        Assert.True(forbidden.EventOnly);

        var honourable = TattooCatalogue.Find("Honoured Tattoo of the Warlord");
        Assert.NotNull(honourable);
        Assert.Equal("Notable (+30 to Strength)", honourable.Target);
        Assert.True(honourable.DropDisabled);
    }

    [Fact]
    public void Limits_use_the_client_limit_categories()
    {
        Assert.Equal("Loyalty Tattoo", TattooCatalogue.LimitName(0));
        Assert.Equal("Attribute Notable Tattoo", TattooCatalogue.LimitName(4));
        Assert.Equal("Ancestral Tattoo", TattooCatalogue.LimitName(5));
        Assert.Equal("Keystone Tattoo", TattooCatalogue.LimitName(6));
        Assert.Equal("Ascendancy Tattoo", TattooCatalogue.LimitName(40));
        Assert.Null(TattooCatalogue.LimitName(7));
        Assert.Null(TattooCatalogue.LimitName(-1));

        Assert.Equal(0, TattooCatalogue.Find("Loyalty Tattoo of Kaom")?.Limit);
        Assert.Equal(4, TattooCatalogue.Find("Honoured Tattoo of the Warlord")?.Limit);
        Assert.Equal(5, TattooCatalogue.Find("Ancestral Tattoo of Bloodlines")?.Limit);
        Assert.Equal(6, TattooCatalogue.Find("Journey Tattoo of Makanui")?.Limit);
        Assert.Equal(40, TattooCatalogue.Find("Forbidden Tattoo of the Ranger")?.Limit);
        Assert.Equal(-1, TattooCatalogue.Find("Tattoo of the Ngamahu Firewalker")?.Limit);
    }

    [Fact]
    public void A_limit_refuses_a_second_tattoo_from_the_same_group_but_allows_replacement()
    {
        var warlord = TattooCatalogue.Find("Honoured Tattoo of the Warlord")!;
        var mountain = TattooCatalogue.Find("Honoured Tattoo of the Mountain")!;
        var makanui = TattooCatalogue.Find("Journey Tattoo of Makanui")!;
        var plain = TattooCatalogue.Find("Tattoo of the Ngamahu Firewalker")!;
        var tattoos = new Dictionary<ushort, string> { [100] = warlord.Name };

        Assert.Equal("only one Attribute Notable Tattoo can be applied",
            Program.TattooLimitRefusal(tattoos, 200, mountain));
        // Replacing the same node with another tattoo of the same group is not a second tattoo.
        Assert.Null(Program.TattooLimitRefusal(tattoos, 100, mountain));
        // A different group and a tattoo without a group are allowed.
        Assert.Null(Program.TattooLimitRefusal(tattoos, 200, makanui));
        Assert.Null(Program.TattooLimitRefusal(tattoos, 200, plain));

        var keystone = new Dictionary<ushort, string> { [100] = makanui.Name };
        Assert.Equal("only one Keystone Tattoo can be applied",
            Program.TattooLimitRefusal(keystone, 200, makanui));
    }

    [Fact]
    public void Every_pilot_tattoo_maps_to_at_least_one_client_stat()
    {
        Assert.NotEmpty(TattooStats.Names);
        foreach (string name in TattooStats.Names)
        {
            Assert.NotNull(TattooCatalogue.Find(name));
            Assert.NotNull(TattooStats.For(name));
        }
    }

    [Fact]
    public void A_fire_tattoo_replaces_the_small_strength_node_in_the_pools()
    {
        TestSaves.Ensure();
        string character = "TattooFire" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // 10221 is a 10-Strength small passive.
            conn.Passives = [10221];
            var without = Program.PoolsFor(conn, 20);
            conn.Tattoos[10221] = "Tattoo of the Ngamahu Firewalker";
            conn.TattooRevision++;
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Strength - 10, with.Strength);
            Assert.Equal(without.FireResistance + 6, with.FireResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_tattoo_only_replaces_its_target_type()
    {
        var strength = Program.Passives.Find(10221);
        Assert.NotNull(strength);
        Assert.True(Program.TargetMatches(strength, "Small Strength"));
        Assert.True(Program.TargetMatches(strength, "Small Attribute"));
        Assert.False(Program.TargetMatches(strength, "Small Dexterity"));
        Assert.False(Program.TargetMatches(strength, "Small Intelligence"));
    }

    [Fact]
    public void Makanga_adjacency_fields_are_loaded_from_the_override()
    {
        var honoured = TattooCatalogue.Find("Honoured Tattoo of the Makanga");
        Assert.NotNull(honoured);
        Assert.Equal(8, honoured.RequiresAdjacent);
        Assert.Equal(0, honoured.MaxAdjacent);

        var ngamahu = TattooCatalogue.Find("Tattoo of the Ngamahu Makanga");
        Assert.NotNull(ngamahu);
        Assert.Equal(0, ngamahu.RequiresAdjacent);
        Assert.Equal(1, ngamahu.MaxAdjacent);

        var hinekora = TattooCatalogue.Find("Tattoo of the Hinekora Makanga");
        Assert.NotNull(hinekora);
        Assert.Equal(7, hinekora.RequiresAdjacent);
        Assert.Equal(0, hinekora.MaxAdjacent);
    }

    [Fact]
    public void Honoured_Makanga_is_inactive_below_eight_allocated_neighbours()
    {
        TestSaves.Ensure();
        string character = "TattooMakanga" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // 3644 is a small Strength with eight neighbours.
            var neighbours = Program.OrdinaryPassives.Adjacent(3644, AscendancyFamily.Normal).Take(8).ToArray();
            Assert.Equal(8, neighbours.Length);
            conn.Tattoos[3644] = "Honoured Tattoo of the Makanga";
            conn.TattooRevision++;

            conn.Passives = [3644, .. neighbours.Take(7)];
            var inactive = Program.PoolsFor(conn, 20);
            conn.Passives = [3644, .. neighbours];
            conn.TattooRevision++;
            var active = Program.PoolsFor(conn, 20);

            Assert.Equal(inactive.FireMaxResistance, active.FireMaxResistance - 2);
            Assert.Equal(inactive.ColdMaxResistance, active.ColdMaxResistance - 2);
            Assert.Equal(inactive.LightningMaxResistance, active.LightningMaxResistance - 2);
            Assert.Equal(inactive.ChaosMaxResistance, active.ChaosMaxResistance - 2);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Conditional_Makanga_stats_are_mapped()
    {
        Assert.Contains(TattooStats.For("Tattoo of the Hinekora Makanga")!,
            stat => stat.Key == Program.Stats.Key("curse_effect_+%"));
        Assert.Contains(TattooStats.For("Tattoo of the Kitava Makanga")!,
            stat => stat.Key == Program.Stats.Key("maximum_life_leech_rate_+%"));
        Assert.Contains(TattooStats.For("Tattoo of the Ramako Makanga")!,
            stat => stat.Key == Program.Stats.Key("number_of_additional_projectiles"));
        Assert.Contains(TattooStats.For("Tattoo of the Rongokurai Makanga")!,
            stat => stat.Key == Program.Stats.Key("chance_to_double_armour_effect_on_hit_%"));
        Assert.Contains(TattooStats.For("Tattoo of the Tasalio Makanga")!,
            stat => stat.Key == Program.Stats.Key("base_cooldown_speed_+%"));
        Assert.Contains(TattooStats.For("Tattoo of the Tawhoa Makanga")!,
            stat => stat.Key == Program.Stats.Key("flask_effect_+%"));
    }

    [Fact]
    public void Journey_Makanui_has_the_48_base_keystones_and_never_picks_the_target()
    {
        Assert.Equal(48, JourneyMakanuiCatalogue.All.Count);
        Assert.Equal("Supreme Ego", JourneyMakanuiCatalogue.FindByGraphId(49639)?.Name);
        Assert.Equal("Versatile Combatant", JourneyMakanuiCatalogue.FindByGraphId(50679)?.Name);
        for (int i = 0; i < 50; i++)
            Assert.NotEqual((ushort)49639, JourneyMakanuiCatalogue.Pick(49639)?.GraphId);
    }

    [Fact]
    public void Journey_Makanui_installs_the_chosen_keystone_stats()
    {
        TestSaves.Ensure();
        string character = "TattooMakanui" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // Versatile Combatant replaced by Supreme Ego.
            conn.Passives = [50679];
            conn.Tattoos[50679] = "Journey Tattoo of Makanui";
            conn.TattooKeystones[50679] = 49639;
            conn.TattooRevision++;
            var stats = Program.CharacterPlayerStats(conn);
            Assert.Equal(1, stats.GetValueOrDefault(Program.Stats.Key("keystone_supreme_ego")!.Value));
            Assert.DoesNotContain(Program.Stats.Key("keystone_versatile_combatant")!.Value, stats.Keys);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Counting_tattoos_aggregate_per_allocated_copy_and_tribe()
    {
        var tattoos = new Dictionary<ushort, string>
        {
            [1] = "Journey Tattoo of the Body",
            [2] = "Journey Tattoo of the Body",
            [3] = "Journey Tattoo of the Mind",
            [4] = "Ancestral Tattoo of Bloodlines",
            [5] = "Tattoo of the Ngamahu Firewalker",
            [6] = "Tattoo of the Arohongui Moonwarden",
        };
        var stats = Program.TattooCountingStats(tattoos, tattoos.Keys.ToHashSet());
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(50, Get("base_maximum_life"));
        Assert.Equal(30, Get("base_maximum_mana"));
        Assert.Equal(0, Get("base_maximum_energy_shield"));
        Assert.Equal(4, Get("life_recovery_rate_+%"));
        Assert.Equal(4, Get("mana_recovery_rate_+%"));
        Assert.Equal(4, Get("energy_shield_recovery_rate_+%"));
    }

    [Fact]
    public void Tribe_recovery_requires_the_ancestral_tattoo()
    {
        var tattoos = new Dictionary<ushort, string>
        {
            [1] = "Tattoo of the Ngamahu Firewalker",
            [2] = "Tattoo of the Arohongui Moonwarden",
        };
        var stats = Program.TattooCountingStats(tattoos, tattoos.Keys.ToHashSet());
        Assert.DoesNotContain(Program.Stats.Key("life_recovery_rate_+%")!.Value, stats.Keys);
    }

    [Fact]
    public void Counting_tattoo_markers_are_mapped()
    {
        foreach (string name in new[] { "Journey Tattoo of the Body", "Journey Tattoo of the Mind",
            "Journey Tattoo of the Soul", "Ancestral Tattoo of Bloodlines" })
            Assert.NotNull(TattooStats.For(name));
    }

    [Fact]
    public void Life_regeneration_rate_increases_the_panel_rate()
    {
        int Get(string name) => name switch
        {
            "base_life_regeneration_rate_per_minute" => 10,
            "life_regeneration_rate_+%" => 16,
            _ => 0,
        };
        Assert.Equal(11.6, PanelStats.LifePerMinute(Get, 1000), 6);
    }

    [Fact]
    public void Ngamahu_Makanga_is_inactive_above_one_allocated_neighbour()
    {
        TestSaves.Ensure();
        string character = "TattooMakangaMax" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Tattoos[10829] = "Tattoo of the Ngamahu Makanga";
            conn.TattooRevision++;

            conn.Passives = [10829, 16167];
            var active = Program.PoolsFor(conn, 20);
            conn.Passives = [10829, 16167, 32091];
            conn.TattooRevision++;
            var inactive = Program.PoolsFor(conn, 20);

            Assert.Equal(inactive.FireMaxResistance + 1, active.FireMaxResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }
}

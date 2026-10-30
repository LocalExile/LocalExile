namespace GameServer.Gateway.Tests;

public class LeechProjectionTests
{
    [Theory]
    [InlineData("life")]
    [InlineData("mana")]
    [InlineData("energy_shield")]
    public void General_and_specific_sources_compose_in_every_applicable_scope(string pool)
    {
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos", "spell" })
        {
            string Alias(string t) => $"{(pool == "energy_shield" ? "virtual_" : "")}{pool}_leech_from_{t}_damage_permyriad";
            var sources = new Dictionary<string, int>
            {
                [$"{pool}_leech_from_any_damage_permyriad"] = 20,
                [$"base_{pool}_leech_from_{type}_damage_permyriad"] = 30,
            };
            var panel = PanelStats.Compute(sources.GetValueOrDefault).ToDictionary(x => x.Stat, x => x.Value);
            Assert.Equal(50, panel[Alias(type)]);
            foreach (string other in new[] { "physical", "fire", "cold", "lightning", "chaos", "spell" }.Except([type]))
                Assert.Equal(20, panel[Alias(other)]);
            Assert.DoesNotContain(panel.Keys, k => k.Contains(pool == "life" ? "mana_leech" : "life_leech"));
        }
    }

    [Theory]
    [InlineData("life")]
    [InlineData("mana")]
    [InlineData("energy_shield")]
    public void Elemental_leech_applies_only_to_the_three_elemental_scopes(string pool)
    {
        var sources = new Dictionary<string, int> { [$"base_{pool}_leech_from_elemental_damage_permyriad"] = 17 };
        var panel = PanelStats.Compute(sources.GetValueOrDefault).ToDictionary(x => x.Stat, x => x.Value);
        foreach (string type in new[] { "fire", "cold", "lightning" })
            Assert.Equal(17, panel[$"{(pool == "energy_shield" ? "virtual_" : "")}{pool}_leech_from_{type}_damage_permyriad"]);
        Assert.DoesNotContain(panel.Keys, k => k.Contains("physical") || k.Contains("chaos") || k.Contains("spell"));
    }

    [Theory]
    [InlineData("life")]
    [InlineData("mana")]
    public void Hand_totals_use_raw_general_physical_attack_and_only_the_qualified_hand(string pool)
    {
        var sources = new Dictionary<string, int>
        {
            [$"{pool}_leech_from_any_damage_permyriad"] = 10,
            [$"base_{pool}_leech_from_physical_damage_permyriad"] = 20,
            [$"base_{pool}_leech_from_attack_damage_permyriad"] = 30,
            [$"{pool}_leech_from_physical_attack_damage_permyriad"] = 40,
            ["life_and_mana_leech_from_physical_damage_permyriad"] = 50,
        };
        var main = new Dictionary<string, int>
        {
            [$"local_{pool}_leech_from_physical_damage_permyriad"] = 6,
            ["local_life_and_mana_leech_from_physical_damage_permyriad"] = 7,
            [$"local_{pool}_leech_from_any_damage_permyriad"] = 8,
        };
        var off = main.ToDictionary(x => x.Key, x => x.Value + 10);
        var panel = PanelStats.Compute(sources.GetValueOrDefault, main.GetValueOrDefault, off.GetValueOrDefault)
            .ToDictionary(x => x.Stat, x => x.Value);
        Assert.Equal(171, panel[$"main_hand_{pool}_leech_from_physical_attack_damage_permyriad"]);
        Assert.Equal(201, panel[$"off_hand_{pool}_leech_from_physical_attack_damage_permyriad"]);
        Assert.Equal(30, panel[$"{pool}_leech_from_physical_damage_permyriad"]); // Combined Life/Mana is attack-only.
        Assert.Equal(10, panel[$"{pool}_leech_from_spell_damage_permyriad"]);
        Assert.Equal(40, panel[$"virtual_{pool}_leech_from_attack_damage_permyriad"]);
        main.Clear(); off.Clear();
        panel = PanelStats.Compute(sources.GetValueOrDefault, main.GetValueOrDefault, off.GetValueOrDefault)
            .ToDictionary(x => x.Stat, x => x.Value);
        Assert.Equal(150, panel[$"main_hand_{pool}_leech_from_physical_attack_damage_permyriad"]);
        Assert.Equal(150, panel[$"off_hand_{pool}_leech_from_physical_attack_damage_permyriad"]);
    }

    [Fact]
    public void Fractional_leech_sources_combine_across_actor_and_support_before_unit_conversion()
    {
        const string raw = "horrible_ugly_life_leech_from_fire_damage_perhundredthousand";
        const string alias = "life_leech_from_fire_damage_permyriad";
        var actor = new Dictionary<string, int> { [raw] = 6 };
        var own = new Dictionary<string, int> { [raw] = 4 };
        int Value() => PanelStats.Selected(actor.GetValueOrDefault, own.GetValueOrDefault).Single(x => x.Stat == alias).Value;
        Assert.Equal(1, Value());
        own[raw] = -6;
        Assert.Equal(0, Value());
        own[raw] = -16;
        Assert.Equal(-1, Value());
    }

    [Fact]
    public void Selected_recovery_speed_composes_both_scoped_and_combined_raw_sources()
    {
        var actor = new Dictionary<string, int> { ["life_mana_es_leech_speed_+%"] = 20 };
        var own = new Dictionary<string, int> { ["life_mana_es_leech_speed_+%"] = 5, ["life_leech_speed_+%"] = 7 };
        var result = PanelStats.Selected(actor.GetValueOrDefault, own.GetValueOrDefault).ToDictionary(x => x.Stat, x => x.Value);
        Assert.Equal(32, result["virtual_life_leech_speed_+%"]);
        Assert.Equal(25, result["virtual_mana_leech_speed_+%"]);
        Assert.Equal(25, result["virtual_energy_shield_leech_speed_+%"]);
    }
}

namespace GameServer.Gateway.Tests;

public class ImpaleStatsTests
{
    static Dictionary<uint, int> Sources(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name)!.Value, s => s.Value);

    [Fact]
    public void General_attack_spell_and_weapon_local_sources_keep_their_scope()
    {
        var actor = Sources(("impale_on_hit_%_chance", 10), ("attacks_impale_on_hit_%_chance", 20),
            ("spells_impale_on_hit_%_chance", 30), ("virtual_total_main_hand_impale_on_hit_%_chance", 100));
        var own = Sources(("impale_on_hit_%_chance", 5), ("attacks_impale_on_hit_%_chance", 7));
        var local = new Dictionary<string, int> { ["local_attacks_impale_on_hit_%_chance"] = 40 };
        Assert.Equal(82, ImpaleStats.From(actor, Program.Stats, true, own, local).Chance);
        Assert.Equal(45, ImpaleStats.From(actor, Program.Stats, false, own, local).Chance);
        Assert.Equal(42, ImpaleStats.From(actor, Program.Stats, true, own).Chance);
    }

    [Fact]
    public void Spell_critical_chance_and_effect_apply_only_to_the_appropriate_hit()
    {
        var actor = Sources(("spell_impale_on_crit_%_chance", 100), ("impale_debuff_effect_+%", 20),
            ("impale_effect_+%_for_impales_inficted_by_spells", 50));
        Assert.Equal(new(0, 20), ImpaleStats.From(actor, Program.Stats, true, critical: true));
        Assert.Equal(new(0, 70), ImpaleStats.From(actor, Program.Stats, false));
        Assert.Equal(new(100, 70), ImpaleStats.From(actor, Program.Stats, false, critical: true));
    }

    [Fact]
    public void Completed_source_totals_set_chance_effect_extra_hits_and_duration_once()
    {
        var actor = Sources(("impale_on_hit_%_chance", int.MaxValue), ("impale_debuff_effect_+%", 20),
            ("impaled_debuff_number_of_reflected_hits", 2), ("impaled_debuff_duration_+%", 50));
        var own = Sources(("impale_on_hit_%_chance", -int.MaxValue), ("attacks_impale_on_hit_%_chance", 60),
            ("impale_debuff_effect_+%", 30), ("impaled_debuff_duration_+%", -25));
        var settings = ImpaleStats.From(actor, Program.Stats, true, own);
        Assert.Equal(new(60, 50, 7, 10), settings);
        actor.Clear(); own.Clear();
        Assert.Equal(new(60, 50, 7, 10), settings); // captured values do not follow later source mutations
        Assert.Equal(new(0), ImpaleStats.From(actor, Program.Stats, true, own));
    }

    [Theory]
    [InlineData(200, 100)]
    [InlineData(-20, 0)]
    public void Panel_and_hit_chances_clamp_the_completed_sum(int source, int expected)
    {
        var actor = new Dictionary<string, int> { ["impale_on_hit_%_chance"] = source };
        var panel = PanelStats.Selected(actor.GetValueOrDefault, _ => 0).ToDictionary(s => s.Stat, s => s.Value);
        foreach (string stat in new[] { "virtual_total_main_hand_impale_on_hit_%_chance",
            "virtual_total_off_hand_impale_on_hit_%_chance", "virtual_total_spell_impale_on_hit_%_chance" })
            Assert.Equal(expected, panel[stat]);
        Assert.Equal(expected, ImpaleStats.From(Sources(("impale_on_hit_%_chance", source)), Program.Stats, true).Chance);
    }
}

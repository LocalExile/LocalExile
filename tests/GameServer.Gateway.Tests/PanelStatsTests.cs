using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PanelStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<StatCatalog> Stats = new(() => new StatCatalog(Data()));

    static Dictionary<string, int> Compute(params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return PanelStats.Compute(n => given.GetValueOrDefault(n)).ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Every_stat_in_the_table_exists_in_Stats_dat()
    {
        var names = PanelStats.Sums.SelectMany(s => s.Sources.Concat(s.Local ?? []).Select(x => x.Name).Prepend(s.Display));
        Assert.DoesNotContain(names, n => Stats.Value.Key(n) is null);
        Assert.Equal(PanelStats.Sums.Length, PanelStats.Sums.Select(s => s.Display).Distinct().Count());
    }

    [Fact]
    public void Attack_only_impale_does_not_become_a_general_spell_or_secondary_source()
    {
        var panel = Compute(("attacks_impale_on_hit_%_chance", 20));
        Assert.Equal(20, panel["virtual_total_main_hand_impale_on_hit_%_chance"]);
        Assert.Equal(20, panel["virtual_total_off_hand_impale_on_hit_%_chance"]);
        Assert.False(panel.ContainsKey("impale_on_hit_%_chance"));
        Assert.False(panel.ContainsKey("virtual_total_spell_impale_on_hit_%_chance"));
    }

    static Dictionary<string, int> Recovery(params (string Stat, int Value)[] stats) =>
        Recovery(1000, 500, stats);

    static Dictionary<string, int> Recovery(uint maxLife, uint maxMana, params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return PanelStats.Recovery(n => given.GetValueOrDefault(n), maxLife, maxMana)
            .ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Without_a_reference_monster_the_estimate_rows_are_left_out()
    {
        var pools = new CharacterStats.Pools(1, 100, 100, 0, 0) with { Armour = 5000, Evasion = 500 };
        var none = Defence(pools, null, null, ("physical_damage_reduction_rating_+%_against_projectiles", 20),
            ("additional_physical_damage_reduction_%_against_hits", 5));
        Assert.DoesNotContain(none.Keys, k => k.Contains("estimated") || k.Contains("against_projectiles") || k.Contains("against_hits"));
        Assert.False(none.ContainsKey("chance_to_evade_%"));
        var damageOnly = Defence(pools, 100, null);
        Assert.True(damageOnly.ContainsKey("display_estimated_physical_damage_reduciton_%"));
        Assert.False(damageOnly.ContainsKey("chance_to_evade_%"));
    }

    static Dictionary<string, int> Defence(CharacterStats.Pools pools, int? damage, int? accuracy, params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return PanelStats.Defence(n => given.GetValueOrDefault(n), pools, damage, accuracy).ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Estimated_physical_reduction_uses_armour_against_the_reference_hit_plus_additional_reduction()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1) with { Armour = 1000, ArmourPct = 50 };
        var panel = Defence(pools, 100, 0);
        Assert.Equal(DamageModel.ArmourReduction(1000, 100), panel["display_estimated_physical_damage_reduciton_%"]);
        Assert.Equal(67, panel["display_estimated_physical_damage_reduciton_%"]);
        Assert.DoesNotContain("armour_differs_against_projectiles", panel.Keys);
        Assert.DoesNotContain("armour_differs_against_hits", panel.Keys);
        panel = Defence(pools, 100, 0, ("base_additional_physical_damage_reduction_%", 10),
            ("physical_damage_reduction_rating_+%_against_projectiles", 30),
            ("additional_physical_damage_reduction_%_against_hits", 5));
        Assert.Equal(77, panel["display_estimated_physical_damage_reduciton_%"]);
        // 1000 armour with 50% increased is 1200 when the 30% projectile increase joins the bucket.
        Assert.Equal(1200, panel["physical_damage_reduction_rating_against_projectiles"]);
        Assert.Equal(1, panel["armour_differs_against_projectiles"]);
        Assert.Equal(DamageModel.ArmourReduction(1200, 100) + 10, panel["display_estimated_physical_damage_reduciton_against_projectiles_%"]);
        Assert.Equal(1, panel["armour_differs_against_hits"]);
        Assert.Equal(82, panel["display_estimated_physical_damage_reduction_against_hits_%"]);
        // The 90% cap applies to the sum.
        Assert.Equal(90, Defence(pools, 1, 0, ("base_additional_physical_damage_reduction_%", 80))["display_estimated_physical_damage_reduciton_%"]);
        Assert.Equal(0, Defence(pools with { Armour = 0 }, 100, 0)["display_estimated_physical_damage_reduciton_%"]);
    }

    [Fact]
    public void Evade_chance_is_the_reference_monster_miss_chance_plus_flat_sources_capped_at_95()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1) with { Evasion = 500 };
        Assert.Equal(DamageModel.EvadeChance(500, 200), Defence(pools, 0, 200)["chance_to_evade_%"]);
        Assert.Equal(DamageModel.EvadeChance(500, 200) + 10, Defence(pools, 0, 200, ("chance_to_evade_attacks_%", 10))["chance_to_evade_%"]);
        Assert.Equal(95, Defence(pools, 0, 200, ("chance_to_evade_attacks_%", 200))["chance_to_evade_%"]);
        Assert.Equal(0, Defence(pools with { Evasion = 0 }, 0, 200)["chance_to_evade_%"]);
    }

    [Fact]
    public void Ward_delay_mana_recharge_and_omniscience_rows_show_with_their_sources()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1) with { Ward = 100, Mana = 300 };
        var none = Defence(pools with { Ward = 0 }, 0, 0);
        Assert.DoesNotContain("ward_delay", none.Keys);
        Assert.DoesNotContain("mana_recharge_rate_per_minute", none.Keys);
        Assert.DoesNotContain("omniscience_for_attribute_requirements", none.Keys);
        Assert.Equal(2000, Defence(pools, 0, 0)["ward_delay"]);
        Assert.Equal(1000, Defence(pools, 0, 0, ("ward_delay_recovery_+%", 100))["ward_delay"]);
        var panel = Defence(pools, 0, 0, ("energy_shield_recharge_apply_to_mana", 1), ("energy_shield_recharge_rate_+%", 50),
            ("attribute_requirements_can_be_satisfied_by_%_of_ascendance", 60), ("base_ascendance", 150), ("ascendance", 50));
        Assert.Equal(9000, panel["mana_recharge_rate_per_minute"]);
        Assert.Equal(120, panel["omniscience_for_attribute_requirements"]);
    }

    [Fact]
    public void Stun_by_kind_flags_and_brand_target_limit_sum_their_sources()
    {
        var panel = Compute(("cannot_be_stunned_by_spells", 3), ("base_number_of_sigils_allowed_per_target", 1));
        Assert.Equal(1, panel["cannot_be_stunned_by_spells"]);
        Assert.DoesNotContain("cannot_be_stunned_by_attacks", panel.Keys);
        Assert.Equal(1, panel["brands_per_target_limit"]);
        var character = new Dictionary<string, int> { ["base_number_of_sigils_allowed_per_target"] = 1 };
        var own = new Dictionary<string, int> { ["base_number_of_sigils_allowed_per_target"] = 1 };
        Assert.Equal(2, PanelStats.Selected(n => character.GetValueOrDefault(n), n => own.GetValueOrDefault(n))
            .Single(s => s.Stat == "brands_per_target_limit").Value);
    }

    [Fact]
    public void Defence_row_stats_exist_in_Stats_dat()
    {
        foreach (var name in new[] { "display_estimated_physical_damage_reduciton_%",
            "display_estimated_physical_damage_reduciton_against_projectiles_%", "armour_differs_against_projectiles",
            "physical_damage_reduction_rating_against_projectiles", "physical_damage_reduction_rating_+%_against_projectiles",
            "display_estimated_physical_damage_reduction_against_hits_%", "armour_differs_against_hits",
            "additional_physical_damage_reduction_%_against_hits", "chance_to_evade_%", "chance_to_evade_attacks_%",
            "ward_delay", "mana_recharge_rate_per_minute", "energy_shield_recharge_apply_to_mana",
            "omniscience_for_attribute_requirements", "attribute_requirements_can_be_satisfied_by_%_of_ascendance",
            "spell_effective_total_chance_permyriad_for_hit_to_be_critical",
            "spell_effective_total_chance_permyriad_for_hit_to_be_double_critical", "spell_critical_strikes_bifurcate",
            "skill_show_average_damage_instead_of_dps", "base_skill_show_average_damage_instead_of_dps" })
            Assert.NotNull(Stats.Value.Key(name));
    }
}

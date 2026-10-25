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
    public void A_display_stat_sums_its_base_form()
    {
        var panel = Compute(("base_stun_recovery_+%", 10), ("base_projectile_speed_+%", 15));
        Assert.Equal(10, panel["stun_recovery_+%"]);
        Assert.Equal(15, panel["projectile_speed_+%"]);
    }

    [Fact]
    public void Combined_stats_count_for_each_row_they_apply_to()
    {
        var panel = Compute(("base_avoid_freeze_%", 20), ("avoid_all_elemental_status_%", 10),
            ("chance_to_freeze_shock_ignite_%", 5), ("base_chance_to_shock_%", 3));
        Assert.Equal(30, panel["avoid_freeze_%"]);
        Assert.Equal(10, panel["avoid_shock_%"]);
        Assert.Equal(5, panel["chance_to_freeze_%"]);
        Assert.Equal(8, panel["chance_to_shock_%"]);
    }

    [Fact]
    public void A_raw_value_of_the_display_stat_is_kept()
    {
        // Haste gives cast_speed_+% itself; a passive gives base_cast_speed_+%.
        Assert.Equal(25, Compute(("cast_speed_+%", 10), ("base_cast_speed_+%", 8), ("attack_and_cast_speed_+%", 7))["cast_speed_+%"]);
    }

    [Fact]
    public void Flags_are_one_when_any_source_is_set()
    {
        var panel = Compute(("base_cannot_be_frozen", 1), ("immune_to_ignite_and_shock", 2));
        Assert.Equal(1, panel["cannot_be_frozen"]);
        Assert.Equal(1, panel["immune_to_ignite"]);
        Assert.Equal(1, panel["immune_to_shock"]);
        Assert.False(panel.ContainsKey("immune_to_freeze"));
    }

    [Fact]
    public void Leech_per_hundred_thousand_is_added_as_permyriad()
    {
        var panel = Compute(("base_life_leech_from_fire_damage_permyriad", 20),
            ("base_life_leech_from_elemental_damage_permyriad", 10),
            ("horrible_ugly_life_leech_from_fire_damage_perhundredthousand", 300));
        Assert.Equal(60, panel["life_leech_from_fire_damage_permyriad"]);
        Assert.Equal(10, panel["life_leech_from_cold_damage_permyriad"]);
    }

    [Fact]
    public void Zero_values_are_not_listed()
    {
        Assert.Empty(Compute());
        Assert.Empty(Compute(("base_avoid_stun_%", 0)));
    }

    [Fact]
    public void Rarity_and_quantity_sum_the_item_mods()
    {
        var panel = Compute(("base_item_found_rarity_+%", 30), ("base_item_found_quantity_+%", 6));
        Assert.Equal(30, panel["display_skill_increased_item_rarity_+%"]);
        Assert.Equal(6, panel["display_skill_increased_item_quantity_+%"]);
    }

    [Fact]
    public void Reduction_stats_count_negative()
    {
        var panel = Compute(("base_self_critical_strike_multiplier_-%", 20), ("action_speed_-%", 5));
        Assert.Equal(-20, panel["extra_damage_taken_from_crit_+%"]);
        Assert.Equal(-5, panel["intermediary_combined_action_speed_+%"]);
    }

    [Fact]
    public void Hand_rows_add_only_their_own_weapons_local_stats()
    {
        var global = new Dictionary<string, int> { ["bleed_on_hit_with_attacks_%"] = 10, ["reduce_enemy_elemental_resistance_%"] = 4 };
        var main = new Dictionary<string, int> { ["local_chance_to_bleed_on_hit_%"] = 25, ["local_fire_penetration_%"] = 6 };
        var panel = PanelStats.Compute(n => global.GetValueOrDefault(n), n => main.GetValueOrDefault(n), _ => 0)
            .ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(35, panel["virtual_total_main_hand_chance_to_bleed_on_hit_%"]);
        Assert.Equal(10, panel["virtual_total_off_hand_chance_to_bleed_on_hit_%"]);
        Assert.Equal(10, panel["main_hand_attack_damage_penetrates_fire_resist_%"]);
        Assert.Equal(4, panel["off_hand_attack_damage_penetrates_fire_resist_%"]);
    }

    [Fact]
    public void Damage_immunities_come_from_their_base_forms()
    {
        var none = PanelStats.Compute(_ => 0).ToDictionary(s => s.Stat, s => s.Value);
        Assert.DoesNotContain("fire_damage_immunity", none.Keys);
        var given = new Dictionary<string, int>
        {
            ["base_fire_immunity"] = 1,
            ["elemental_damage_immunity"] = 1,
            ["chaos_immunity"] = 1,
            ["physical_immunity"] = 1,
        };
        var panel = PanelStats.Compute(n => given.GetValueOrDefault(n)).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(1, panel["fire_damage_immunity"]);
        Assert.Equal(1, panel["cold_damage_immunity"]);
        Assert.Equal(1, panel["lightning_damage_immunity"]);
        Assert.Equal(1, panel["chaos_damage_immunity"]);
        Assert.Equal(1, panel["physical_immunity"]);
    }

    [Fact]
    public void Damage_immunity_stats_exist_in_Stats_dat()
    {
        foreach (var name in new[] { "fire_damage_immunity", "cold_damage_immunity", "lightning_damage_immunity",
            "chaos_damage_immunity", "physical_immunity", "base_fire_immunity", "base_cold_immunity",
            "base_lightning_immunity", "elemental_damage_immunity", "chaos_immunity" })
            Assert.NotNull(Stats.Value.Key(name));
    }

    [Fact]
    public void Pool_rows_add_their_stat_and_cap()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1) with
        {
            BlockChanceUncapped = 70, BlockChanceMax = 75, SpellBlockChance = 20, SpellBlockChanceMax = 75, ChaosResistance = -30
        };
        var given = new Dictionary<string, int> { ["additional_block_chance_against_projectiles_%"] = 10,
            ["base_spell_projectile_block_%"] = 15, ["additional_chaos_resistance_against_damage_over_time_%"] = 25 };
        var panel = PanelStats.FromPools(n => given.GetValueOrDefault(n), pools).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(75, panel["projectile_attack_block_%"]);
        Assert.Equal(35, panel["spell_projectile_block_%"]);
        Assert.Equal(1, panel["spell_block_differs_against_projectiles"]);
        Assert.Equal(-5, panel["chaos_damage_over_time_resistance_%"]);
        Assert.Empty(PanelStats.FromPools(_ => 0, pools));
    }

    [Fact]
    public void Pool_row_stats_exist_in_Stats_dat()
    {
        foreach (var name in new[] { "projectile_attack_block_%", "spell_projectile_block_%", "spell_block_differs_against_projectiles",
            "uncapped_chaos_damage_over_time_resistance_%", "chaos_damage_over_time_resistance_%",
            "additional_block_chance_against_projectiles_%", "base_spell_projectile_block_%",
            "additional_chaos_resistance_against_damage_over_time_%" })
            Assert.NotNull(Stats.Value.Key(name));
    }

    [Fact]
    public void Every_hand_damage_row_exists_in_Stats_dat()
    {
        var packet = DamagePacket.Of(DamageType.Physical, 5, 12).Add(DamageType.Fire, new DamageRange(3, 7));
        Assert.All(PanelStats.HandDamage(packet, "main_hand"), r => Assert.NotNull(Stats.Value.Key(r.Stat)));
    }

    [Fact]
    public void The_hand_damage_rows_carry_each_type_and_the_total()
    {
        var packet = DamagePacket.Of(DamageType.Physical, 5, 12).Add(DamageType.Fire, new DamageRange(3, 7));
        var rows = PanelStats.HandDamage(packet, "main_hand").ToDictionary(r => r.Stat, r => r.Value);
        Assert.Equal(8, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(19, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(5, rows["main_hand_minimum_physical_damage"]);
        Assert.Equal(12, rows["main_hand_maximum_physical_damage"]);
        Assert.Equal(3, rows["main_hand_minimum_fire_damage"]);
        Assert.Equal(7, rows["main_hand_maximum_fire_damage"]);
    }

    [Fact]
    public void An_empty_attack_adds_no_damage_rows()
    {
        Assert.Empty(PanelStats.HandDamage(DamagePacket.Empty, "main_hand"));
    }

    [Fact]
    public void Impale_chance_sums_the_attacks_form()
    {
        var panel = Compute(("attacks_impale_on_hit_%_chance", 20));
        Assert.Equal(20, panel["impale_on_hit_%_chance"]);
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
    public void Regeneration_rows_are_the_per_minute_totals()
    {
        // 60 flat per minute plus 2% of 1000 maximum life; the client divides by 60 for the per-second row.
        var panel = Recovery(("base_life_regeneration_rate_per_minute", 60), ("life_regeneration_rate_per_minute_%", 2));
        Assert.Equal(80, panel["total_life_recovery_per_minute_from_regeneration"]);
        Assert.Equal(80, panel["life_recovery_per_minute"]);
        // Character.ot's 105% of maximum mana per minute with no other source.
        Assert.Equal(525, panel["total_mana_recovery_per_minute_from_regeneration"]);
        Assert.Equal(525, panel["mana_recovery_per_minute"]);
    }

    [Fact]
    public void Mana_recovery_adds_the_flat_rate_and_the_increase()
    {
        var panel = Recovery(("base_mana_regeneration_rate_per_minute", 1200), ("mana_regeneration_rate_per_minute_%", 20),
            ("mana_regeneration_rate_+%", 40));
        // (105 + 20)% of 500 + 1200 flat = 1825, raised by 40%.
        Assert.Equal(2555, panel["total_mana_recovery_per_minute_from_regeneration"]);
        Assert.Equal(2555, panel["mana_recovery_per_minute"]);
    }

    [Fact]
    public void Without_a_life_regeneration_source_the_row_is_absent()
    {
        Assert.False(Recovery().ContainsKey("total_life_recovery_per_minute_from_regeneration"));
        Assert.False(Recovery().ContainsKey("life_recovery_per_minute"));
    }

    [Fact]
    public void Regeneration_rows_exist_in_Stats_dat()
    {
        foreach (var name in new[] { "total_life_recovery_per_minute_from_regeneration",
            "total_mana_recovery_per_minute_from_regeneration", "life_recovery_per_minute", "mana_recovery_per_minute",
            "base_life_regeneration_rate_per_minute",
            "life_regeneration_rate_per_minute_%", "base_mana_regeneration_rate_per_minute",
            "mana_regeneration_rate_per_minute_%", "mana_regeneration_rate_+%" })
            Assert.NotNull(Stats.Value.Key(name));
    }
}

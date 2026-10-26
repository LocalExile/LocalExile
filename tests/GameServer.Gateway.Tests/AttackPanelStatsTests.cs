using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AttackPanelStatsTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new("data"));
    static readonly Lazy<StatCatalog> Stats = new(() => new("data"));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static SkillCatalog.Unarmed Weapon(int min = 10, int max = 20) => new(714, min, max, 8);
    static DamagePacket Resolve(string effect, IReadOnlyDictionary<uint, int> character, SkillCatalog.Unarmed? weapon = null, int quality = 0)
    {
        int row = Skills.Value.EffectRow(effect);
        return AttackSkillHit.Resolve(weapon ?? Weapon(), character, Skills.Value.EffectStats(row, 1),
            Stats.Value, Skills.Value.DamageEffectiveness(row, 1), Skills.Value.IsMelee(row), Skills.Value.QualityStats(row, quality),
            baseMultiplier: Skills.Value.AttackBaseMultiplier(row, 1));
    }
    static Dictionary<string, int> Rows(string effect, DamagePacket hit, int aps = 140) =>
        AttackPanelStats.Compute(SkillHitPlan.ForSkill(Skills.Value, Skills.Value.EffectRow(effect), 1), hit, aps)
            .ToDictionary(r => r.Stat, r => r.Value);

    [Theory]
    [InlineData("PowerSiphonAltX", -4000, 6000)]
    [InlineData("BurningArrow", 18000, 28000)]
    [InlineData("HeavyStrike", 12430, 22430)]
    [InlineData("FlickerStrike", 7730, 17730)]
    [InlineData("PlayerMelee", 0, 10000)]
    public void Actual_base_and_added_damage_columns_are_deltas_from_the_identity_factor(
        string effectId, int rawDelta, int normalized)
    {
        int effect = Skills.Value.EffectRow(effectId);
        using var effects = JsonDocument.Parse(File.ReadAllBytes("data/tables/GrantedEffects.json"));
        using var levels = JsonDocument.Parse(File.ReadAllBytes("data/tables/GrantedEffectStatSetsPerLevel.json"));
        int set = effects.RootElement[effect].GetProperty("StatSet1").GetInt32();
        var row = levels.RootElement.EnumerateArray().Single(r => r.GetProperty("StatSet").GetInt32() == set
            && r.GetProperty("GemLevel").GetInt32() == 1);
        Assert.Equal(rawDelta, row.GetProperty("BaseMultiplier").GetInt32());
        Assert.Equal(rawDelta, row.GetProperty("DamageEffectiveness").GetInt32());
        Assert.Equal(normalized, Skills.Value.AttackBaseMultiplier(effect, 1));
        Assert.Equal(normalized, Skills.Value.DamageEffectiveness(effect, 1));
    }

    [Fact]
    public void Unequal_normalized_base_and_added_factors_scale_their_own_sources_before_Rage_and_conversion()
    {
        var character = Values(("attack_minimum_added_physical_damage", 4), ("attack_maximum_added_physical_damage", 8),
            ("attack_damage_+%", 50), ("attack_damage_+%_final_from_rage", 30),
            ("skill_physical_damage_%_to_convert_to_fire", 100));
        var hit = AttackSkillHit.Resolve(Weapon(), character, [], Stats.Value, 2500, melee: false, baseMultiplier: 15000);
        // ((10..20)*1.5 + (4..8)*.25) * 1.5 increased attack damage * 1.3 Rage, converted once.
        Assert.Equal(31.2, hit[DamageType.Fire].Min, 8);
        Assert.Equal(62.4, hit[DamageType.Fire].Max, 8);
        Assert.Equal(DamageRange.Zero, hit[DamageType.Physical]);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10000, 10000, 14)]
    [InlineData(0, 10000, 4)]
    [InlineData(10000, 0, 10)]
    public void Explicit_zero_base_or_added_factors_remain_zero_while_identity_preserves_that_source(
        int baseMultiplier, int addedEffectiveness, double expected)
    {
        var character = Values(("attack_minimum_added_physical_damage", 4), ("attack_maximum_added_physical_damage", 4));
        var hit = AttackSkillHit.Resolve(Weapon(10, 10), character, [], Stats.Value, addedEffectiveness,
            melee: false, baseMultiplier: baseMultiplier);
        Assert.Equal(expected, hit.MinTotal, 8);
        Assert.Equal(expected, hit.MaxTotal, 8);
    }

    [Fact]
    public void Real_Burning_Arrow_uses_280_percent_base_and_added_effectiveness_and_full_skill_fire_conversion()
    {
        int effect = Skills.Value.EffectRow("BurningArrow");
        Assert.Equal(28000, Skills.Value.DamageEffectiveness(effect, 1));
        Assert.Equal(28000, Skills.Value.AttackBaseMultiplier(effect, 1));
        Assert.Contains(Skills.Value.EffectStats(effect, 1), s =>
            s.Item1 == Stats.Value.Key("skill_physical_damage_%_to_convert_to_fire") && s.Item2 == 100);
        var hit = Resolve("BurningArrow", Values());
        Assert.Equal(new DamageRange(28, 56), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(0, 0), hit[DamageType.Physical]);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(28, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(56, rows["main_hand_maximum_fire_damage"]);
        Assert.Equal(0, rows["main_hand_maximum_physical_damage"]);
        Assert.Equal(4200, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(5880, rows["hundred_times_damage_per_second"]);
        // A separately corrected APS value is consumed directly, without recomputing skill speed here.
        Assert.Equal(4116, Rows("BurningArrow", hit, 98)["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Real_Heavy_Strike_keeps_fractional_physical_damage_until_display_flooring()
    {
        Assert.Equal(22430, Skills.Value.DamageEffectiveness(Skills.Value.EffectRow("HeavyStrike"), 1));
        var hit = Resolve("HeavyStrike", Values(), Weapon(10, 10));
        Assert.Equal(22.43, hit.MinTotal, 6);
        Assert.Equal(22.43, hit.MaxTotal, 6);
        var rows = Rows("HeavyStrike", hit);
        Assert.Equal(22, rows["main_hand_minimum_physical_damage"]);
        Assert.Equal(22, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(2243, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(3140, rows["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Captured_five_to_thirteen_bow_fixture_scales_and_converts_the_inherited_physical_display()
    {
        var character = Values(("melee_physical_damage_+%", 2));
        var weapon = Weapon(5, 13);
        var ordinary = weapon.Stats(character, AttackSkillHit.BaseHit(weapon, character, Stats.Value, melee: false))
            .ToDictionary(kv => kv.Item1, kv => kv.Item2);
        Assert.Equal(900, ordinary[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
        var hit = Resolve("BurningArrow", character, weapon);
        Assert.Equal(14, hit.MinTotal, 6);
        Assert.Equal(36.4, hit.MaxTotal, 6);
        Assert.Equal(25.2, hit.AverageTotal, 6);
        var rows = Rows("BurningArrow", hit, 98);
        Assert.Equal(14, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(36, rows["main_hand_maximum_fire_damage"]);
        Assert.Equal(0, rows["main_hand_maximum_physical_damage"]);
        Assert.Equal(2520, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(2470, rows["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Added_attack_damage_and_current_character_increases_are_scaled_before_conversion()
    {
        var player = Values(("damage_+%", 10), ("cold_damage_+%", 100),
            ("attack_minimum_added_cold_damage", 2), ("attack_maximum_added_cold_damage", 4),
            ("spell_minimum_added_fire_damage", 1000), ("spell_maximum_added_fire_damage", 2000));
        var hit = Resolve("BurningArrow", player);
        Assert.Equal(30.8, hit[DamageType.Fire].Min, 6);
        Assert.Equal(61.6, hit[DamageType.Fire].Max, 6);
        Assert.Equal(11.76, hit[DamageType.Cold].Min, 6);
        Assert.Equal(23.52, hit[DamageType.Cold].Max, 6);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(42, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(85, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(6384, rows["display_hundred_times_damage_per_skill_use"]);
    }

    [Fact]
    public void Character_conversion_wins_while_supported_skill_damage_inputs_add_before_the_shared_packet()
    {
        var character = Values(("skill_physical_damage_%_to_convert_to_fire", 25), ("damage_+%", 10));
        var skill = Values(("skill_physical_damage_%_to_convert_to_fire", 100),
            ("damage_+%", 20), ("attack_minimum_added_fire_damage", 2), ("attack_maximum_added_fire_damage", 4));
        var hit = AttackSkillHit.Resolve(Weapon(), character, skill.Select(kv => (kv.Key, kv.Value)), Stats.Value, 18000, melee: false);
        Assert.Equal(17.55, hit[DamageType.Physical].Min, 6);
        Assert.Equal(35.1, hit[DamageType.Physical].Max, 6);
        Assert.Equal(10.53, hit[DamageType.Fire].Min, 6);
        Assert.Equal(21.06, hit[DamageType.Fire].Max, 6);
        Assert.Equal(42.12, hit.AverageTotal, 6);
    }

    [Theory]
    [InlineData(0, 0, 90)]
    [InlineData(20, 0, 108)]
    [InlineData(0, 6, 126)]
    [InlineData(20, 6, 144)]
    public void Archmage_Power_Siphon_adds_its_own_level_quality_and_Whisperer_mana_sources_once(int quality,
        int globalPercent, double expected)
    {
        int effect = Skills.Value.EffectRow("PowerSiphonAltX");
        uint key = Stats.Value.Key(DamageStats.MaxManaAddedLightning)!.Value;
        Assert.Contains((key, 15), Skills.Value.EffectStats(effect, 1));
        if (quality == 20) Assert.Contains((key, 3), Skills.Value.QualityStats(effect, quality));
        Assert.Equal(6000, Skills.Value.DamageEffectiveness(effect, 1));
        var character = Values(("maximum_mana", 1000), (DamageStats.MaxManaAddedLightning, globalPercent));
        var hit = Resolve("PowerSiphonAltX", character, Weapon(0, 0), quality);
        Assert.Equal(new DamageRange(expected, expected), hit[DamageType.Lightning]);
        Assert.Equal(globalPercent, character[key]);
        // Same raw sources feed ranged damage, with attack increases and final Rage; melee-only increase stays out.
        character[Stats.Value.Key("attack_damage_+%")!.Value] = 50;
        character[Stats.Value.Key("melee_damage_+%")!.Value] = 100;
        character[Stats.Value.Key("attack_damage_+%_final_from_rage")!.Value] = 30;
        var scaled = Resolve("PowerSiphonAltX", character, Weapon(0, 0), quality);
        Assert.Equal(expected * 1.5 * 1.3, scaled[DamageType.Lightning].Min, 6);
    }

    [Fact]
    public void Gain_as_extra_uses_the_same_converted_combat_packet()
    {
        var hit = Resolve("BurningArrow", Values(("physical_damage_%_to_add_as_chaos", 20)));
        Assert.Equal(new DamageRange(28, 56), hit[DamageType.Fire]);
        Assert.Equal(5.6, hit[DamageType.Chaos].Min, 6);
        Assert.Equal(11.2, hit[DamageType.Chaos].Max, 6);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(33, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(67, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(5040, rows["display_hundred_times_damage_per_skill_use"]);
    }

    [Fact]
    public void Creation_and_refresh_use_raw_character_inputs_instead_of_derived_weapon_ranges()
    {
        var character = Values(("melee_physical_damage_+%", 2));
        var rawBefore = new Dictionary<uint, int>(character);
        var weapon = Weapon(5, 9);
        // WeaponStats supplies the unrounded packet to the ordinary attack; its legacy fallback floors first.
        var ordinary = weapon.Stats(character, AttackSkillHit.BaseHit(weapon, character, Stats.Value, melee: false))
            .ToDictionary(kv => kv.Item1, kv => kv.Item2);
        Dictionary<uint, int> Panel(int staleDamage)
        {
            var final = new Dictionary<uint, int>(ordinary);
            final[StatKeys.MainHandMinTotal] = staleDamage;
            final[StatKeys.MainHandMaxTotal] = staleDamage;
            final[StatKeys.DisplayHundredTimesDamagePerSkillUse] = staleDamage;
            foreach (var (name, value) in Rows("BurningArrow", Resolve("BurningArrow", character, weapon), 98))
                final[Stats.Value.Key(name)!.Value] = value;
            return final;
        }
        var created = Panel(999);
        var refreshed = Panel(99999);
        Assert.Equal(created.OrderBy(kv => kv.Key), refreshed.OrderBy(kv => kv.Key));
        Assert.Equal(14, created[StatKeys.MainHandMinTotal]);
        Assert.Equal(25, created[StatKeys.MainHandMaxTotal]);
        Assert.Equal(1960, created[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
        Assert.Equal(1921, created[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal(rawBefore.OrderBy(kv => kv.Key), character.OrderBy(kv => kv.Key));
        Assert.Equal(700, ordinary[StatKeys.DisplayHundredTimesDamagePerSkillUse]); // Ranged attacks exclude Strength's melee bonus.
    }

    [Fact]
    public void Absent_types_and_zero_hits_clear_all_inherited_mainhand_hit_fields()
    {
        var rows = Rows("BurningArrow", Resolve("BurningArrow", Values()));
        Assert.Equal(14, rows.Count);
        foreach (string type in new[] { "physical", "cold", "lightning", "chaos" })
        {
            Assert.Equal(0, rows[$"main_hand_minimum_{type}_damage"]);
            Assert.Equal(0, rows[$"main_hand_maximum_{type}_damage"]);
        }
        Assert.All(Rows("BurningArrow", DamagePacket.Empty), row => Assert.Equal(0, row.Value));
        var noRate = Rows("BurningArrow", Resolve("BurningArrow", Values()), -1);
        Assert.Equal(4200, noRate["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(0, noRate["hundred_times_damage_per_second"]);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("ScorchingRay")]
    [InlineData("RighteousFire")]
    [InlineData("EnduringCry")]
    public void Spells_and_nonattack_actions_keep_their_display_path(string effect) =>
        Assert.Empty(Rows(effect, DamagePacket.Of(DamageType.Fire, 100, 200)));

    [Fact]
    public void Critical_and_accuracy_inputs_do_not_weight_the_precrit_average()
    {
        var baseline = Rows("BurningArrow", Resolve("BurningArrow", Values()));
        var critical = Rows("BurningArrow", Resolve("BurningArrow", Values(
            ("main_hand_critical_strike_chance", 10000), ("main_hand_critical_strike_multiplier", 300),
            ("main_hand_chance_to_hit_%", 50))));
        Assert.Equal(baseline.OrderBy(kv => kv.Key), critical.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void Quality_gain_conversion_adds_once_after_character_precedence_and_matches_current_conversion_overlay()
    {
        var character = Values(("damage_+%", 10), ("physical_damage_%_to_add_as_cold", 5));
        int effect = Skills.Value.EffectRow("IceCrash");
        Assert.Contains((Stats.Value.Key("physical_damage_%_to_add_as_cold")!.Value, 15), Skills.Value.QualityStats(effect, 20));
        var hit = Resolve("IceCrash", character, Weapon(10, 10), quality: 20);
        var conversionInputs = Skills.Value.EffectStatsFor(effect, 1, 20, character, characterStatsWin: true);
        var expected = SpellDamage.WithEffectiveness(AttackSkillHit.BaseHit(Weapon(10, 10), character, Stats.Value, melee: true),
                Skills.Value.DamageEffectiveness(effect, 1))
            .Convert(DamageStats.Conversion(conversionInputs, Stats.Value, attack: true));
        Assert.Equal(expected.Components, hit.Components);
        var withoutQuality = Resolve("IceCrash", character, Weapon(10, 10));
        Assert.Equal(11 * Skills.Value.DamageEffectiveness(effect, 1) / 10000.0 * .15,
            hit[DamageType.Cold].Min - withoutQuality[DamageType.Cold].Min, 8);
        Assert.Equal(5, character[Stats.Value.Key("physical_damage_%_to_add_as_cold")!.Value]);
    }

    [Fact]
    public void Effect_melee_context_scopes_base_and_added_damage_before_derived_increases_and_Rage()
    {
        var character = Values(("melee_damage_+%", 1000), ("attack_minimum_added_physical_damage", 6),
            ("attack_maximum_added_physical_damage", 12), (DerivedDamageIncreases.SpellToAttack, 1), ("spell_damage_+%", 1),
            (DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", 1), ("attack_damage_+%_final_from_rage", 30));
        var ranged = Resolve("BurningArrow", character, Weapon(5, 13));
        Assert.Equal(11 * 1.03 * 1.3 * 2.8, ranged.MinTotal, 8);
        Assert.Equal(25 * 1.03 * 1.3 * 2.8, ranged.MaxTotal, 8);
        var melee = Resolve("HeavyStrike", character, Weapon(5, 13));
        Assert.Equal(11 * 11.03 * 1.3 * 2.243, melee.MinTotal, 8);
        Assert.Equal(25 * 11.03 * 1.3 * 2.243, melee.MaxTotal, 8);
    }

    [Theory]
    [InlineData("MH_TotalDamage", "total", 670, 671)]
    [InlineData("MH_PhysicalDamage", "physical", 279, 280)]
    [InlineData("MH_FireDamage", "fire", 267, 268)]
    [InlineData("MH_ColdDamage", "cold", 263, 264)]
    [InlineData("MH_LightningDamage", "lightning", 271, 272)]
    [InlineData("MH_ChaosDamage", "chaos", 275, 276)]
    public void Native_mainhand_range_fields_use_whole_numbers_and_existing_hand_spell_gates(
        string id, string type, uint minKey, uint maxKey)
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "CharacterPanelStats.json")));
        var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == id);
        Assert.Equal(minKey, Stats.Value.Key($"main_hand_minimum_{type}_damage"));
        Assert.Equal(maxKey, Stats.Value.Key($"main_hand_maximum_{type}_damage"));
        Assert.Equal(new[] { minKey - 1, maxKey - 1 }, row.GetProperty("StatsKeys1").EnumerateArray().Select(v => v.GetUInt32()));
        Assert.Contains(7791, row.GetProperty("StatsKeys2").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(new[] { 437, 1674 }, row.GetProperty("StatsKeys3").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Empty(row.GetProperty("__col8").EnumerateArray());
    }
}

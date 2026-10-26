using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class EnduringCryTests
{
    static readonly SkillCatalog Skills = new();
    static readonly StatCatalog Stats = new();
    static int Effect => Skills.EffectRow(EnduringCry.Effect);
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Key(v.Name)!.Value, v => v.Value);
    static EnduringCry.Profile Profile(int level = 1, int quality = 0, params (string Name, int Value)[] values) =>
        Assert.IsType<EnduringCry.Profile>(EnduringCry.For(Skills, Stats, Effect, level, quality, Values(values)));
    static EnduringCry.Target Normal(double distance = 0) => new(MonsterRarity.Normal, distance);

    [Theory]
    [InlineData(1, 0, 2500, 13, 800)]
    [InlineData(20, 38, 3400, 19, 580)]
    [InlineData(21, 40, 3500, 20, 571)]
    [InlineData(30, 49, 3900, 22, 537)]
    public void Current_tables_supply_level_specific_speed_duration_cost_and_raw_time(
        int level, int speed, int duration, int mana, int useMs)
    {
        var p = Profile(level);
        Assert.True(Skills.IsWarcry(Effect));
        Assert.False(Skills.IsSpell(Effect));
        Assert.False(Skills.IsAttack(Effect));
        Assert.Equal(800, p.Timing.BaseMilliseconds);
        Assert.Equal(speed, p.Timing.IncreasedWarcrySpeed);
        Assert.Equal(useMs, p.Timing.DurationMilliseconds);
        Assert.Equal(duration, p.DurationMilliseconds);
        Assert.Equal(mana, p.BaseManaCost);
        Assert.Equal(8000, p.BaseCooldownMilliseconds);
        Assert.Equal(1, p.StoredUses);
        Assert.Equal(60, p.Radius);
        Assert.Equal(1541, EnduringCry.BuffDefinition);
    }

    [Theory]
    [InlineData(0, 0, 267, 11.428571428571429)]
    [InlineData(20, 0, 267, 7.2727272727272725)]
    [InlineData(0, 18, 226, 11.428571428571429)]
    public void Call_of_the_Ancestors_and_quality_affect_independent_speed_and_recovery_inputs(
        int quality, int actionPercent, int useMs, double cooldown)
    {
        var p = Profile(1, quality, ("warcry_speed_+%", 200), ("warcry_cooldown_speed_+%", -30),
            ("virtual_action_speed_+%", actionPercent), ("cast_speed_+%", 999));
        Assert.Equal(useMs, p.Timing.DurationMilliseconds);
        Assert.Equal(cooldown, p.CooldownSeconds, 9);
        Assert.Equal(8000, p.BaseCooldownMilliseconds);
    }

    [Fact]
    public void Generic_projected_cooldown_and_own_quality_add_once_without_action_speed()
    {
        var p = Profile(20, 20, ("base_cooldown_speed_+%", 25), ("virtual_cooldown_speed_+%", 25),
            ("warcry_cooldown_speed_+%", -30), ("enduring_cry_cooldown_speed_+%", 15),
            ("virtual_action_speed_+%", 100));
        Assert.Equal(50, p.RecoveryPercent); //25generic+40quality-30warcry+15specific
        Assert.Equal(8 / 1.5, p.CooldownSeconds, 9);
        Assert.Equal(290, p.Timing.DurationMilliseconds); //800/1.38/2, rounded once
    }

    [Theory]
    [InlineData(20, 0, 333)]
    [InlineData(20, 1, 267)]
    [InlineData(100, 0, null)]
    public void The_current_action_floor_applies_to_warcry_time_without_changing_recovery(
        int actionReduction, int floor, int? milliseconds)
    {
        var p = Profile(1, 0, ("warcry_speed_+%", 200), ("action_speed_-%", actionReduction),
            ("action_speed_cannot_be_reduced_below_base", floor));
        Assert.Equal(milliseconds, p.Timing.DurationMilliseconds);
        Assert.Equal(8, p.CooldownSeconds);
    }

    [Fact]
    public void Actual_Call_of_the_Ancestors_node_supplies_both_independent_modifiers()
    {
        var node = new PassiveTree().Stats([48740]);
        var p = Assert.IsType<EnduringCry.Profile>(EnduringCry.For(Skills, Stats, Effect, 20, 0, node));
        Assert.Equal(238, p.Timing.IncreasedWarcrySpeed); //38skilllevel+200node
        Assert.Equal(237, p.Timing.DurationMilliseconds);
        Assert.Equal(-30, p.RecoveryPercent);
        Assert.Equal(8 / .7, p.CooldownSeconds, 9);
    }

    [Theory]
    [InlineData(4, 4, 0, 0)]
    [InlineData(5, 5, 1, 120)]
    [InlineData(25, 25, 5, 600)]
    [InlineData(40, 40, 8, 600)]
    public void Whole_power_groups_grant_charges_while_regeneration_counts_at_most_twenty_five_power(
        int enemies, int power, int charges, int regen)
    {
        var result = EnduringCry.Resolve(Profile(), Enumerable.Repeat(Normal(), enemies));
        Assert.Equal(power, result.Power);
        Assert.Equal(charges, result.Charges);
        Assert.Equal(regen, result.RegenPerMinute);
    }

    [Theory]
    [InlineData(MonsterRarity.Normal, 1, 0, 0)]
    [InlineData(MonsterRarity.Magic, 2, 0, 0)]
    [InlineData(MonsterRarity.Rare, 10, 2, 240)]
    [InlineData(MonsterRarity.Unique, 20, 4, 480)]
    public void Rarity_power_oracles_are_independent_from_the_charge_maximum(
        MonsterRarity rarity, int power, int charges, int regen)
    {
        var result = EnduringCry.Resolve(Profile(), [new(rarity, 0)]);
        Assert.Equal(power, result.Power);
        Assert.Equal(charges, result.Charges);
        Assert.Equal(regen, result.RegenPerMinute);
    }

    [Fact]
    public void Dead_owned_friendly_nonfinite_and_out_of_range_targets_contribute_no_power()
    {
        var result = EnduringCry.Resolve(Profile(),
        [
            new(MonsterRarity.Unique, 1, Alive: false),
            new(MonsterRarity.Unique, 1, Owned: true),
            new(MonsterRarity.Unique, 1, Hostile: false),
            new(MonsterRarity.Unique, double.NaN), new(MonsterRarity.Unique, -1),
            new(MonsterRarity.Unique, 60.001), new(MonsterRarity.Rare, 60),
        ]);
        Assert.Equal(10, result.Power);
        Assert.Equal(2, result.Charges);
    }

    [Fact]
    public void Increased_power_keeps_fractions_until_the_final_whole_power_group()
    {
        var p = Profile(1, 0, ("warcry_monster_power_+%", 25));
        var result = EnduringCry.Resolve(p, Enumerable.Repeat(Normal(), 4));
        Assert.Equal(5, result.Power);
        Assert.Equal(1, result.Charges);
        Assert.Equal(120, result.RegenPerMinute);
    }

    [Fact]
    public void Buff_effect_duration_and_charge_duration_are_distinct_scopes()
    {
        var p = Profile(1, 0, ("skill_effect_duration_+%", 20), ("warcry_duration_+%", 15),
            ("charge_duration_+%", 999), ("warcry_buff_effect_+%", 25), ("enduring_cry_buff_effect_+%", 25));
        var result = EnduringCry.Resolve(p, [new(MonsterRarity.Unique, 0)]);
        Assert.Equal(3375, p.DurationMilliseconds);
        Assert.Equal(4, result.Charges);
        Assert.Equal(720, result.RegenPerMinute); //8%life/sec from20power, then50%buffeffect=>12%
    }

    [Theory]
    [InlineData(100, 0, 960)]
    [InlineData(100, 50, 1200)]
    [InlineData(-100, 0, 0)]
    [InlineData(-25, 25, 480)]
    public void Owner_buff_effect_adds_once_with_warcry_effect_before_regeneration(int self, int warcry, int expected)
    {
        var profile = Profile(1, 0, ("buff_effect_on_self_+%", self), ("warcry_buff_effect_+%", warcry));
        var result = EnduringCry.Resolve(profile, [new(MonsterRarity.Unique, 0)]);
        Assert.Equal(expected, result.RegenPerMinute);
        Assert.Equal(4, result.Charges);
        Assert.Equal(2500, result.DurationMilliseconds);
    }

    [Fact]
    public void Warcry_area_joins_combined_generic_area_once_and_ignores_spell_only_area()
    {
        var p = Profile(1, 0, ("base_skill_area_of_effect_+%", 50), ("skill_area_of_effect_+%", 50),
            ("warcry_skill_area_of_effect_+%", 50), ("spell_area_of_effect_+%", 300));
        Assert.Equal(84, p.Radius); //50combinedgeneric+50warcry=>100%area
    }

    [Fact]
    public void Minimum_infinite_and_additional_charge_inputs_do_not_change_the_regeneration_power_cap()
    {
        var minimum = EnduringCry.Resolve(Profile(1, 0, ("warcries_have_minimum_10_power", 1)), []);
        Assert.Equal(10, minimum.Power);
        Assert.Equal(2, minimum.Charges);
        Assert.Equal(240, minimum.RegenPerMinute);
        var extra = EnduringCry.Resolve(Profile(1, 0, ("enduring_cry_grants_x_additional_endurance_charges", 1)), []);
        Assert.Equal(1, extra.Charges);
        Assert.Equal(0, extra.RegenPerMinute);
        var infinite = EnduringCry.Resolve(Profile(1, 0, ("warcries_have_infinite_power", 1)), []);
        Assert.Equal(int.MaxValue, infinite.Charges);
        Assert.Equal(600, infinite.RegenPerMinute);
    }

    [Fact]
    public void Rampager_disables_the_warcry_but_spell_damage_restrictions_do_not()
    {
        var blocked = Profile(1, 0, ("your_warcry_skills_are_disabled", 1));
        Assert.False(blocked.CanResolveOwnerEffect);
        Assert.Equal(default, EnduringCry.Resolve(blocked, [new(MonsterRarity.Unique, 0)]));
        Assert.True(Profile(1, 0, ("your_spells_are_disabled", 1), ("spell_skills_deal_no_damage", 1)).CanResolveOwnerEffect);
    }

    [Fact]
    public void Instant_and_nonpositive_warcry_speed_are_explicit_timing_outcomes()
    {
        Assert.Equal(0, Profile(1, 0, ("warcries_are_instant", 1)).Timing.DurationMilliseconds);
        var blocked = Profile(1, 0, ("warcry_speed_+%", -100));
        Assert.Null(blocked.Timing.DurationMilliseconds);
        Assert.False(blocked.CanResolveOwnerEffect);
        Assert.Null(WarcryTiming.ForSkill(Skills, Stats, Skills.EffectRow("Fireball"), 1, 0, Values()));
        Assert.Null(EnduringCry.For(Skills, Stats, Skills.EffectRow("Fireball"), 1, 0, Values()));
    }

    [Theory]
    [InlineData(1, 0, 0, 800, 125)]
    [InlineData(20, 0, 0, 580, 173)]
    [InlineData(20, 200, 0, 237, 423)]
    [InlineData(20, 200, 22, 194, 423)]
    public void Native_projection_keeps_raw_base_and_nonspell_rate_separate_from_the_final_server_wait(
        int level, int warcrySpeed, int actionSpeed, int wait, int rawRate)
    {
        var player = Values(("warcry_speed_+%", warcrySpeed), ("virtual_action_speed_+%", actionSpeed),
            ("cast_speed_+%", 999));
        var native = Skills.EffectStatsFor(Effect, level, 0, player);
        int Get(string name) => native.GetValueOrDefault(Stats.Key(name)!.Value);
        var timing = WarcryTiming.ForSkill(Skills, Stats, Effect, level, 0, player)!.Value;
        Assert.Equal(wait, timing.DurationMilliseconds);
        Assert.Equal(wait, Skills.CastTimeMs(Effect, level, 0, player));
        Assert.Equal(800, Get("base_spell_cast_time_ms"));
        Assert.Equal(800, Get("virtual_base_spell_cast_time_ms"));
        Assert.Equal(rawRate, Get("hundred_times_non_spell_casts_per_second"));
        Assert.Equal(rawRate, timing.NativeNonSpellRate);
        Assert.Equal(warcrySpeed + (level == 20 ? 38 : 0), Get("warcry_speed_+%"));
        Assert.Equal(actionSpeed, Get("virtual_action_speed_+%"));
        Assert.Equal(1, Get("is_warcry"));
        Assert.Equal(0, Get("casting_spell"));
    }

    [Fact]
    public void Native_rate_uses_half_away_rounding_without_quantized_milliseconds_or_action_speed()
    {
        var timing = WarcryTiming.Calculate(800, 238, 1.22);
        Assert.Equal(194, timing.DurationMilliseconds);
        Assert.Equal(515, timing.HundredTimesUsesPerSecond);
        Assert.Equal(423, timing.NativeNonSpellRate); //125*3.38=422.5, independently rounded half away.
        Assert.Equal(173, WarcryTiming.Calculate(800, 38).NativeNonSpellRate); //172.5, not banker's172.
        Assert.Equal(423, WarcryTiming.Calculate(800, 238, 1.22, instant: true).NativeNonSpellRate);
    }

    [Theory]
    [InlineData("warcries_are_instant")]
    [InlineData("base_skill_is_instant")]
    public void Final_instant_flag_is_derived_without_turning_a_passive_into_intrinsic_instant(string input)
    {
        var player = Values((input, 1));
        var native = Skills.EffectStatsFor(Effect, 20, 0, player);
        Assert.Equal(1, native[Stats.Key("skill_is_instant")!.Value]);
        Assert.Equal(input == "base_skill_is_instant" ? 1 : 0,
            native.GetValueOrDefault(Stats.Key("base_skill_is_instant")!.Value));
        Assert.Equal(800, native[Stats.Key("virtual_base_spell_cast_time_ms")!.Value]);
        Assert.Equal(173, native[Stats.Key("hundred_times_non_spell_casts_per_second")!.Value]);
        Assert.Equal(0, Skills.CastTimeMs(Effect, 20, 0, player));
        var refunded = Skills.EffectStatsFor(Effect, 20, 0, Values());
        Assert.Equal(0, refunded[Stats.Key("skill_is_instant")!.Value]);
        Assert.Equal(580, Skills.CastTimeMs(Effect, 20, 0, Values()));
    }

    [Fact]
    public void Explicit_character_wins_mode_preserves_its_final_input_override()
    {
        var native = Skills.EffectStatsFor(Effect, 20, 0, Values(("warcry_speed_+%", 200)), characterStatsWin: true);
        Assert.Equal(200, native[Stats.Key("warcry_speed_+%")!.Value]);
        Assert.Equal(375, native[Stats.Key("hundred_times_non_spell_casts_per_second")!.Value]);
    }

    [Theory]
    [InlineData("base_spell_cast_time_ms_override", 500)]
    [InlineData("warcry_speed_+%_if_not_warcried_recently", 100)]
    [InlineData("skill_is_retaliation_skill", 1)]
    [InlineData("skill_is_steel_skill_reload", 1)]
    public void Unmodeled_native_timing_inputs_have_no_executable_profile(string stat, int value)
    {
        Assert.Null(EnduringCry.For(Skills, Stats, Effect, 1, 0, Values((stat, value))));
        var own = Skills.EffectStats(Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        own[Stats.Key(stat)!.Value] = value;
        Assert.Null(EnduringCry.For(Skills, Stats, Effect, 1, 0, Values(), own));
    }

    [Fact]
    public void Selected_cooldown_metadata_exposes_group_bypass_and_pause_instead_of_losing_them()
    {
        foreach (int level in new[] { 1, 20 })
        {
            var metadata = Assert.IsType<SkillCooldownInfo>(Skills.CooldownInfo(Effect, level));
            Assert.Equal(8000, metadata.Milliseconds);
            Assert.Equal(1, metadata.StoredUses);
            Assert.Equal(1, metadata.Group);
            Assert.Equal(4, metadata.BypassType);
            Assert.False(metadata.PausesWhileActive);
        }
        Assert.Null(Skills.CooldownInfo(-1, 1));
        Assert.Null(new SkillCatalog("missing-enduring-cry-data").CooldownInfo(Effect, 1));
        Assert.Equal(2, Skills.CooldownInfo(Skills.EffectRow("FlickerStrike"), 1)!.BypassType);
        Assert.Equal(36, Skills.CooldownInfo(Skills.EffectRow("FlickerStrike"), 1)!.Group);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_invalid_cooldown_fields_stay_unknown(bool invalid)
    {
        string folder = Path.Combine(Path.GetTempPath(), "enduring-cry-metadata-" + Guid.NewGuid().ToString("N"));
        string tables = Path.Combine(folder, "tables");
        Directory.CreateDirectory(tables);
        string[] files = ["GrantedEffects.json", "GrantedEffectsPerLevel.json", "GrantedEffectStatSetsPerLevel.json"];
        try
        {
            var effect = new Dictionary<string, object?> { ["Id"] = "EnduringCry", ["StatSet1"] = 0, ["CastTime"] = 800 };
            var level = new Dictionary<string, object?>
                { ["GrantedEffect"] = 0, ["Level"] = 1, ["Cooldown"] = 8000, ["StoredUses"] = 1 };
            if (invalid)
            {
                effect["CooldownNotRecoverDuringActive"] = "false";
                level["CooldownGroup"] = -1;
                level["CooldownBypassType"] = 4.5;
            }
            File.WriteAllText(Path.Combine(tables, files[0]), System.Text.Json.JsonSerializer.Serialize(new[] { effect }));
            File.WriteAllText(Path.Combine(tables, files[1]), System.Text.Json.JsonSerializer.Serialize(new[] { level }));
            File.WriteAllText(Path.Combine(tables, files[2]), "[{\"StatSet\":0,\"GemLevel\":1}]");
            var info = Assert.IsType<SkillCooldownInfo>(new SkillCatalog(folder).CooldownInfo(0, 1));
            Assert.Equal(8000, info.Milliseconds);
            Assert.Equal(1, info.StoredUses);
            Assert.Null(info.Group);
            Assert.Null(info.BypassType);
            Assert.Null(info.PausesWhileActive);
        }
        finally
        {
            foreach (string file in files) File.Delete(Path.Combine(tables, file));
            Directory.Delete(tables);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Enduring_Cry_uses_the_canonical_one_use_budget_with_typed_recovery_and_quality_once()
    {
        var own = Skills.EffectStats(Effect, 20, 20).ToDictionary(s => s.Item1, s => s.Item2);
        var plan = SingleUseCooldown.ForSkill(Skills, Stats, Effect, 20,
            Values(("virtual_cooldown_speed_+%", 25), ("base_cooldown_speed_+%", 25),
                ("warcry_cooldown_speed_+%", -30), ("enduring_cry_cooldown_speed_+%", 15)), own)!.Value;
        Assert.Equal(8000, plan.BaseMilliseconds);
        Assert.Equal(50, plan.RecoveryPercent);
        var state = new SingleUseCooldownState(0, plan.RecoveryRate);
        Assert.True(state.TryUse(0, plan));
        Assert.Equal(8 / 1.5, state.RemainingSeconds, 9);
        Assert.False(state.TryUse(5, plan));
        Assert.True(state.TryUse(8 / 1.5, plan));
        Assert.Null(SingleUseCooldown.ForSkill(Skills, Stats, Skills.EffectRow("RallyingCry"), 1, Values(), own));
    }

    [Theory]
    [InlineData("skill_cooldown_group_override", 36)]
    [InlineData("skill_ignores_cooldown", 1)]
    [InlineData("virtual_skill_ignores_cooldown", 1)]
    [InlineData("warcry_skills_share_cooldowns", 1)]
    [InlineData("warcry_cooldown_modifier_ms", -100)]
    [InlineData("warcry_skills_cooldown_is_4_seconds", 1)]
    [InlineData("non_instant_warcries_have_no_cooldown", 1)]
    [InlineData("base_added_cooldown_count", 1)]
    [InlineData("skill_cooldown_time_override_ms", 1000)]
    public void Unsupported_Enduring_Cry_cooldown_inputs_are_explicitly_ineligible(string stat, int value)
    {
        var own = Skills.EffectStats(Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Null(SingleUseCooldown.ForSkill(Skills, Stats, Effect, 1, Values((stat, value)), own));
        own[Stats.Key(stat)!.Value] = value;
        Assert.Null(SingleUseCooldown.ForSkill(Skills, Stats, Effect, 1, Values(), own));
    }

    [Fact]
    public void Supported_own_inputs_are_used_once_for_timing_and_buff_duration()
    {
        var own = Skills.EffectStats(Effect, 20, 20).ToDictionary(s => s.Item1, s => s.Item2);
        own[Stats.Key("warcry_speed_+%")!.Value] += 100;
        own[Stats.Key("skill_effect_duration_+%")!.Value] = 50;
        var profile = EnduringCry.For(Skills, Stats, Effect, 20, 20, Values(("warcry_speed_+%", 200)), own)!;
        Assert.Equal(338, profile.Timing.IncreasedWarcrySpeed);
        Assert.Equal(183, profile.Timing.DurationMilliseconds);
        Assert.Equal(5100, profile.DurationMilliseconds);
        Assert.Equal(40, profile.RecoveryPercent);
    }
}

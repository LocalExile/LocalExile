namespace GameServer.Gateway.Tests;

public sealed class SkillQualityMergeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("localexile-skill-quality-").FullName;
    private readonly SkillCatalog _skills;
    private readonly StatCatalog _stats;

    public SkillQualityMergeTests()
    {
        string tables = Directory.CreateDirectory(Path.Combine(_folder, "tables")).FullName;
        File.WriteAllText(Path.Combine(tables, "Stats.json"), """
            [{"Id":"base_cast_speed_+%"},{"Id":"cast_speed_+%"},{"Id":"damage_+%"},
             {"Id":"base_skill_area_of_effect_+%"},{"Id":"active_skill_base_area_of_effect_radius"},
             {"Id":"virtual_base_spell_cast_time_ms"},{"Id":"base_spell_cast_time_ms"},
             {"Id":"hundred_times_casts_per_second"},{"Id":"base_mana_cost"},{"Id":"mana_cost"},
             {"Id":"attack_and_cast_speed_+%"},{"Id":"skill_area_of_effect_+%"}]
            """);
        File.WriteAllText(Path.Combine(tables, "GrantedEffects.json"), """[{"Id":"FixtureSpell","StatSet1":0,"CastTime":1300,"ActiveSkill":0}]""");
        File.WriteAllText(Path.Combine(tables, "ActiveSkills.json"), """[{"ActiveSkillTypes":[0]}]""");
        File.WriteAllText(Path.Combine(tables, "ActiveSkillType.json"), """[{"Id":"Spell"}]""");
        File.WriteAllText(Path.Combine(tables, "GrantedEffectsPerLevel.json"), """[{"GrantedEffect":0,"Level":1,"CostTypes":[0],"CostAmounts":[5]}]""");
        File.WriteAllText(Path.Combine(tables, "GrantedEffectStatSets.json"), """[{"ImplicitStats":[],"ConstantStats":[],"ConstantStatsValues":[]}]""");
        File.WriteAllText(Path.Combine(tables, "GrantedEffectStatSetsPerLevel.json"), """
            [{"StatSet":0,"GemLevel":1,"FloatStats":[4],"BaseResolvedValues":[18],"AdditionalStats":[],"AdditionalStatsValues":[]}]
            """);
        File.WriteAllText(Path.Combine(tables, "GrantedEffectQualityStats.json"), """
            [{"GrantedEffectsKey":0,"StatsKeys":[0,2,3,4],"StatsValuesPermille":[1000,1000,1000,150]}]
            """);
        _skills = new(_folder);
        _stats = new(_folder);
    }

    private uint Key(string name) => _stats.Key(name)!.Value;

    [Theory]
    [InlineData(-10000, -10000, 0, 0)]
    [InlineData(0, 0, 10000, 10000)]
    [InlineData(5000, -7500, 15000, 2500)]
    public void Raw_damage_deltas_decode_independently_and_keep_explicit_zero_distinct_from_identity(
        int rawBaseDelta, int rawAddedDelta, int expectedBase, int expectedAdded)
    {
        File.WriteAllText(Path.Combine(_folder, "tables", "GrantedEffectStatSetsPerLevel.json"),
            System.Text.Json.JsonSerializer.Serialize(new[] { new { StatSet = 0, GemLevel = 1,
                BaseMultiplier = rawBaseDelta, DamageEffectiveness = rawAddedDelta } }));
        var skills = new SkillCatalog(_folder);
        Assert.Equal(expectedBase, skills.AttackBaseMultiplier(0, 1));
        Assert.Equal(expectedAdded, skills.DamageEffectiveness(0, 1));
    }

    [Fact]
    public void Missing_damage_columns_and_missing_effect_rows_keep_identity()
    {
        Assert.Equal(10000, _skills.AttackBaseMultiplier(0, 1));
        Assert.Equal(10000, _skills.DamageEffectiveness(0, 1));
        Assert.Equal(10000, _skills.AttackBaseMultiplier(-1, 1));
        Assert.Equal(10000, _skills.DamageEffectiveness(-1, 1));
    }

    [Theory]
    [InlineData(0, 10, 1182, 18)]
    [InlineData(20, 30, 1000, 21)]
    public void Quality_adds_to_global_modifiers_while_native_base_and_final_server_delay_stay_distinct(int quality, int increase, int expectedMs, int expectedRadius)
    {
        var character = new Dictionary<uint, int>
        {
            [Key("base_cast_speed_+%")] = 10, [Key("cast_speed_+%")] = 10,
            [Key("damage_+%")] = 10, [Key("base_skill_area_of_effect_+%")] = 10, [Key("skill_area_of_effect_+%")] = 10,
            [Key("active_skill_base_area_of_effect_radius")] = 999, [Key("mana_cost")] = 999,
        };
        var merged = _skills.EffectStatsFor(0, 1, quality, character);
        Assert.Equal(increase, merged[Key("base_cast_speed_+%")]);
        Assert.Equal(increase, merged[Key("cast_speed_+%")]);
        Assert.Equal(increase, merged[Key("damage_+%")]);
        Assert.Equal(increase, merged[Key("base_skill_area_of_effect_+%")]);
        Assert.Equal(increase, merged[Key("skill_area_of_effect_+%")]);
        Assert.Equal(expectedRadius, merged[Key("active_skill_base_area_of_effect_radius")]);
        Assert.Equal(5, merged[Key("mana_cost")]); // intrinsic level stats keep their existing precedence
        Assert.Equal(1300, merged[Key("virtual_base_spell_cast_time_ms")]);
        Assert.Equal(expectedMs, _skills.CastTimeMs(0, 1, quality, character));
        Assert.Equal(10, character[Key("damage_+%")]); // no mutation or double counting on refresh
        Assert.Equal(merged[Key("damage_+%")], _skills.EffectStatsFor(0, 1, quality, character)[Key("damage_+%")]);
    }

    [Fact]
    public void Raw_cast_speed_without_a_cached_final_value_is_also_counted_once()
    {
        var merged = _skills.EffectStatsFor(0, 1, 20, new Dictionary<uint, int> { [Key("base_cast_speed_+%")] = 10 });
        Assert.Equal(30, merged[Key("cast_speed_+%")]);
        Assert.Equal(1300, merged[Key("virtual_base_spell_cast_time_ms")]);
        Assert.Equal(1000, _skills.CastTimeMs(0, 1, 20, new Dictionary<uint, int> { [Key("base_cast_speed_+%")] = 10 }));
    }

    [Fact]
    public void Final_area_includes_base_and_quality_once_for_simulation_and_client_stats()
    {
        var character = new Dictionary<uint, int>
        {
            [Key("base_skill_area_of_effect_+%")] = 10, [Key("skill_area_of_effect_+%")] = 10,
        };
        var merged = _skills.EffectStatsFor(0, 1, 20, character);
        int Get(string name) => _stats.Key(name) is uint key ? merged.GetValueOrDefault(key) : 0;
        Assert.Equal(30, merged[Key("skill_area_of_effect_+%")]);
        Assert.Equal(30, SkillArea.Increased(Get, spell: true, combined: true));
        // Base radius 18 + quality 3 = 21; floor(21*floor(100*sqrt(1.3))/100) = 23.
        Assert.Equal(23, SkillArea.Radius(21, Get, spell: true, combined: true));
    }

    [Theory]
    [InlineData("LightningWarp")]
    [InlineData("Desecrate")]
    public void Actual_cast_speed_quality_adds_twenty_percent_to_ten_percent_global_speed(string effect)
    {
        if (Data() is not { } data) return;
        var skills = new SkillCatalog(data);
        var stats = new StatCatalog(data);
        uint Key(string name) => stats.Key(name)!.Value;
        int row = skills.EffectRow(effect);
        var player = new Dictionary<uint, int> { [Key("base_cast_speed_+%")] = 10, [Key("cast_speed_+%")] = 10 };
        var zero = skills.EffectStatsFor(row, 1, 0, player);
        var twenty = skills.EffectStatsFor(row, 1, 20, player);
        Assert.Equal(10, zero[Key("cast_speed_+%")]);
        Assert.Equal(30, twenty[Key("cast_speed_+%")]);
        // Actual target-table CastTime is 600 ms for both skills: 600/1.1 rounds to 545; 600/1.3 to 462.
        Assert.Equal(545, skills.CastTimeMs(row, 1, 0, player));
        Assert.Equal(462, skills.CastTimeMs(row, 1, 20, player));
        Assert.Equal(600, twenty[Key("virtual_base_spell_cast_time_ms")]);
    }

    [Fact]
    public void Actual_damage_and_area_quality_preserve_global_modifiers()
    {
        if (Data() is not { } data) return;
        var skills = new SkillCatalog(data);
        var stats = new StatCatalog(data);
        uint damage = stats.Key("damage_+%")!.Value, area = stats.Key("base_skill_area_of_effect_+%")!.Value;
        var player = new Dictionary<uint, int> { [damage] = 10, [area] = 10 };
        Assert.Equal(10, skills.EffectStatsFor(skills.EffectRow("VaalFlameblast"), 1, 0, player)[damage]);
        Assert.Equal(30, skills.EffectStatsFor(skills.EffectRow("VaalFlameblast"), 1, 20, player)[damage]);
        Assert.Equal(10, skills.EffectStatsFor(skills.EffectRow("Haste"), 1, 0, player)[area]);
        Assert.Equal(50, skills.EffectStatsFor(skills.EffectRow("Haste"), 1, 20, player)[area]);
        uint radius = stats.Key("active_skill_base_area_of_effect_radius")!.Value;
        int fireball = skills.EffectRow("Fireball");
        Assert.Equal(skills.EffectStatsFor(fireball, 1, 0, player)[radius] + 3,
            skills.EffectStatsFor(fireball, 1, 20, player)[radius]);
    }

    private static string? Data()
    {
        string[] required = ["Stats", "GrantedEffects", "GrantedEffectsPerLevel", "GrantedEffectStatSets",
            "GrantedEffectStatSetsPerLevel", "GrantedEffectQualityStats"];
        return required.All(name => File.Exists(Path.Combine("data", "tables", name + ".json"))) ? "data" : null;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}

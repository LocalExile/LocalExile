using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class SkillAttackTimingTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new(BenchCraftingTests.Data()));

    [Theory]
    [InlineData("BurningArrow", -30)]
    [InlineData("HeavyStrike", -15)]
    [InlineData("FlickerStrike", 20)]
    [InlineData("CausticArrow", 0)]
    [InlineData("PlayerMelee", 0)]
    public void Extracted_inherent_speed_is_not_an_additive_player_increase(string effect, int expected)
    {
        int row = Skills.Value.EffectRow(effect);
        Assert.Equal(expected, Skills.Value.AttackSpeedMultiplier(row, 1));
        Assert.Equal(expected, Skills.Value.AttackSpeedMultiplier(row, 20));
    }

    [Fact]
    public void Catalog_selects_the_effects_nearest_level_and_missing_field_keeps_ordinary_timing()
    {
        string folder = Directory.CreateTempSubdirectory("attack-timing-").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "tables"));
        void Table(string name, object value) => File.WriteAllText(Path.Combine(folder, "tables", name + ".json"), JsonSerializer.Serialize(value));
        Table("GrantedEffects", new[] { new { Id = "test", StatSet1 = -1 } });
        Table("GrantedEffectsPerLevel", new object[]
        {
            new { GrantedEffect = 0, Level = 1, AttackSpeedMultiplier = -30 },
            new { GrantedEffect = 0, Level = 10, AttackSpeedMultiplier = 20 },
            new { GrantedEffect = 0, Level = 20 },
        });
        Table("GrantedEffectStatSetsPerLevel", Array.Empty<object>());
        var skills = new SkillCatalog(folder);
        Assert.Equal(-30, skills.AttackSpeedMultiplier(0, 9));
        Assert.Equal(20, skills.AttackSpeedMultiplier(0, 10));
        Assert.Equal(20, skills.AttackSpeedMultiplier(0, 19));
        Assert.Equal(0, skills.AttackSpeedMultiplier(0, 20));
        Assert.Equal(0, skills.AttackSpeedMultiplier(-1, 1));
        Assert.Equal(0, skills.AttackSpeedMultiplier(int.MaxValue, 1));
        Assert.Equal(0, skills.AttackSpeedMultiplier(0, 0));
    }

    [Fact]
    public void Inherent_rate_multiplies_the_weapon_before_player_speed_and_rounds_the_native_base_once()
    {
        int inherent = Skills.Value.AttackSpeedMultiplier(Skills.Value.EffectRow("BurningArrow"), 1);
        Assert.Equal(1429, SkillAttackTiming.BaseMilliseconds(1000, inherent));
        Assert.Equal(.7145, SkillAttackTiming.Seconds(1000, 100, inherent), 8);
        Assert.NotEqual(1 / 1.7, SkillAttackTiming.Seconds(1000, 100, inherent));
        Assert.Equal(694, SkillAttackTiming.BaseMilliseconds(833, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-80)]
    public void Zero_inherent_multiplier_keeps_existing_ordinary_attack_duration(int speed)
    {
        var weapon = new SkillCatalog.Unarmed(714, 2, 5, 8);
        var player = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = speed };
        Assert.Equal(weapon.AttackSeconds(player), SkillAttackTiming.Seconds(weapon.AttackMs, speed, 0));
    }

    [Fact]
    public void Skill_stats_change_total_bases_and_APS_once_without_changing_player_speed_or_weapon_base()
    {
        var stats = new Dictionary<uint, int>
        {
            [StatKeys.MainHandAttackDurationMs] = 1000,
            [StatKeys.MainHandTotalAttackDurationMs] = 1000,
            [StatKeys.MainHandAttackSpeedPct] = 100,
            [StatKeys.OffHandAttackDurationMs] = 800,
            [StatKeys.OffHandTotalAttackDurationMs] = 800,
            [StatKeys.OffHandAttackSpeedPct] = 50,
        };
        SkillAttackTiming.Apply(stats, -30);
        Assert.Equal(1000, stats[StatKeys.MainHandAttackDurationMs]);
        Assert.Equal(1429, stats[StatKeys.MainHandTotalAttackDurationMs]);
        Assert.Equal(100, stats[StatKeys.MainHandAttackSpeedPct]);
        Assert.Equal(800, stats[StatKeys.OffHandAttackDurationMs]);
        Assert.Equal(1143, stats[StatKeys.OffHandTotalAttackDurationMs]);
        Assert.Equal(50, stats[StatKeys.OffHandAttackSpeedPct]);
        Assert.Equal(140, stats[StatKeys.HundredTimesAttacksPerSecond]);
        var first = new Dictionary<uint, int>(stats);
        SkillAttackTiming.Apply(stats, -30);
        Assert.Equal(first.OrderBy(kv => kv.Key), stats.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void Tick_cap_is_applied_after_both_factors_and_invalid_data_is_finite()
    {
        Assert.Equal(Program.TickSeconds, SkillAttackTiming.Seconds(1000, 10000, 20));
        Assert.Equal(5.0 / 101, SkillAttackTiming.Seconds(1000, 10000, -80), 8);
        Assert.Equal(1000, SkillAttackTiming.BaseMilliseconds(1000, -100));
        Assert.Equal(1, SkillAttackTiming.BaseMilliseconds(1, int.MaxValue));
        Assert.Equal(int.MaxValue, SkillAttackTiming.BaseMilliseconds(int.MaxValue, -99));
    }

    [Fact]
    public void Real_attack_route_uses_the_same_duration_for_native_stats_pacing_and_contact()
    {
        int effect = Skills.Value.EffectRow("BurningArrow");
        var weapon = new SkillCatalog.Unarmed(1000, 2, 5, 8);
        var player = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 100 };
        double seconds = SkillAttackTiming.ForSkill(Skills.Value, effect, 1, weapon, player);
        var native = weapon.Stats(player).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        SkillAttackTiming.Apply(Skills.Value, effect, 1, native);
        double nativeSeconds = native[StatKeys.MainHandTotalAttackDurationMs] / 1000.0
            / (1 + native[StatKeys.MainHandAttackSpeedPct] / 100.0);
        Assert.Equal(seconds, nativeSeconds);
        Assert.Equal((int)Math.Round(100 / seconds), native[StatKeys.HundredTimesAttacksPerSecond]);
        double next = AttackTiming.After(0, 0, seconds);
        Assert.False(AttackTiming.Ready(.5, next, seconds));
        Assert.True(AttackTiming.Ready(.55, next, seconds));
        Assert.Equal(.7145 * .374, AttackTiming.ContactDelay(seconds, .374, 0), 8);
    }

    [Fact]
    public void Captured_714ms_bow_keeps_default_timing_but_Burning_Arrow_resolves_to_1020ms()
    {
        var weapon = new SkillCatalog.Unarmed(714, 2, 5, 8);
        var player = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 0 };
        var ordinary = weapon.Stats(player).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        Assert.Equal(714, ordinary[StatKeys.MainHandTotalAttackDurationMs]);
        Assert.Equal(140, ordinary[StatKeys.HundredTimesAttacksPerSecond]);
        var burning = new Dictionary<uint, int>(ordinary);
        int effect = Skills.Value.EffectRow("BurningArrow");
        SkillAttackTiming.Apply(Skills.Value, effect, 1, burning);
        Assert.Equal(714, burning[StatKeys.MainHandAttackDurationMs]);
        Assert.Equal(0, burning[StatKeys.MainHandAttackSpeedPct]);
        Assert.Equal(1020, burning[StatKeys.MainHandTotalAttackDurationMs]);
        Assert.Equal(98, burning[StatKeys.HundredTimesAttacksPerSecond]);
        Assert.Equal(1.02, SkillAttackTiming.ForSkill(Skills.Value, effect, 1, weapon, player), 8);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("ScorchingRay")]
    [InlineData("EnduringCry")]
    public void Nonattack_skills_do_not_receive_attack_timing_or_APS_overrides(string effect)
    {
        var weapon = new SkillCatalog.Unarmed(1000, 2, 5, 8);
        var player = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 100 };
        var native = weapon.Stats(player).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var before = new Dictionary<uint, int>(native);
        int row = Skills.Value.EffectRow(effect);
        SkillAttackTiming.Apply(Skills.Value, row, 1, native);
        Assert.Equal(before.OrderBy(kv => kv.Key), native.OrderBy(kv => kv.Key));
        Assert.Equal(weapon.AttackSeconds(player), SkillAttackTiming.ForSkill(Skills.Value, row, 1, weapon, player));
    }
}

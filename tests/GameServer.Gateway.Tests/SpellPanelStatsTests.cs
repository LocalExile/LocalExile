using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SpellPanelStatsTests
{
    static readonly Lazy<StatCatalog> Stats = new(() => new(Data()));
    static readonly Lazy<SkillCatalog> Skills = new(() => new(Data()));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static Dictionary<string, int> Rows(SkillHitPlan plan, DamagePacket hit, int cps = 100) =>
        SpellPanelStats.Compute(plan, hit, cps).ToDictionary(r => r.Stat, r => r.Value);

    [Fact]
    public void Real_fireball_uses_its_modified_packet_with_floored_ranges_and_fractional_average()
    {
        int effect = Skills.Value.EffectRow("Fireball");
        var plan = SkillHitPlan.ForSkill(Skills.Value, effect, 1);
        var own = Skills.Value.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var hit = SpellHit.Resolve(plan, Stats.Value, Values(("spell_damage_+%", 12)), own, new DamageConversion());
        var rows = Rows(plan, hit, 133);
        Assert.Equal(10.08, hit.MinTotal, 6);
        Assert.Equal(15.68, hit.MaxTotal, 6);
        Assert.Equal(10, rows["spell_minimum_total_damage"]);
        Assert.Equal(15, rows["spell_maximum_total_damage"]);
        Assert.Equal(10, rows["spell_minimum_fire_damage"]);
        Assert.Equal(15, rows["spell_maximum_fire_damage"]);
        Assert.Equal(0, rows["spell_maximum_physical_damage"]);
        Assert.Equal(1288, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(1713, rows["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Mixed_conversion_and_gain_as_extra_are_in_both_the_total_and_each_type()
    {
        var plan = SkillHitPlan.Create(false, true,
            DamagePacket.Of(DamageType.Physical, 10, 20).Add(DamageType.Cold, new(3, 7)));
        var conversion = DamageStats.Conversion(Values(("physical_damage_%_to_convert_to_fire", 50),
            ("physical_damage_%_to_add_as_chaos", 20)), Stats.Value, attack: false);
        var hit = SpellHit.Resolve(plan, Stats.Value, Values(), Values(), conversion);
        var rows = Rows(plan, hit, 150);
        Assert.Equal(new DamageRange(5, 10), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(5, 10), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(3, 7), hit[DamageType.Cold]);
        Assert.Equal(new DamageRange(2, 4), hit[DamageType.Chaos]);
        Assert.Equal(15, rows["spell_minimum_total_damage"]);
        Assert.Equal(31, rows["spell_maximum_total_damage"]);
        Assert.Equal(2, rows["spell_minimum_chaos_damage"]);
        Assert.Equal(4, rows["spell_maximum_chaos_damage"]);
        Assert.Equal(2300, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(3450, rows["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Added_spell_damage_uses_effectiveness_and_only_applicable_type_and_projectile_increases()
    {
        var plan = SkillHitPlan.Create(false, true, DamagePacket.Of(DamageType.Fire, 9, 14));
        var player = Values(("spell_minimum_added_cold_damage", 3), ("spell_maximum_added_cold_damage", 5),
            ("attack_minimum_added_fire_damage", 1000), ("cold_damage_+%", 100), ("projectile_damage_+%", 50));
        var hit = SpellHit.Resolve(plan, Stats.Value, player, Values(("base_is_projectile", 1)), new DamageConversion(), 2);
        Assert.Equal(new DamageRange(13.5, 21), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(15, 25), hit[DamageType.Cold]);
        var rows = Rows(plan, hit);
        Assert.Equal(28, rows["spell_minimum_total_damage"]);
        Assert.Equal(46, rows["spell_maximum_total_damage"]);
        Assert.Equal(3725, rows["display_hundred_times_damage_per_skill_use"]);
    }

    [Fact]
    public void Existing_modifier_overlay_and_separate_level_only_conversion_precedence_are_preserved()
    {
        var plan = SkillHitPlan.Create(false, true, DamagePacket.Of(DamageType.Physical, 100, 100));
        var player = Values(("spell_damage_+%", 50), ("physical_damage_%_to_convert_to_fire", 25));
        var finalPlayer = Values(("spell_damage_+%", 50), ("physical_damage_%_to_convert_to_fire", 99),
            ("spell_minimum_total_damage", 999), ("spell_maximum_total_damage", 999));
        var ownWithQuality = Values(("spell_damage_+%", 100), ("physical_damage_%_to_convert_to_fire", 90));
        var ownLevelOnly = Values(("physical_damage_%_to_convert_to_fire", 75));
        var conversion = DamageStats.Conversion(player, ownLevelOnly.Select(kv => (kv.Key, kv.Value)), Stats.Value, false);
        // The final display dictionary is not the character-input dictionary ConversionFor uses.
        var hit = SpellHit.Resolve(plan, Stats.Value, finalPlayer, ownWithQuality, conversion);
        Assert.Equal(new DamageRange(150, 150), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(50, 50), hit[DamageType.Fire]);
        // With no player collision, use 75% from the level-only inputs, not the quality overlay's 90%.
        conversion = DamageStats.Conversion(Values(), ownLevelOnly.Select(kv => (kv.Key, kv.Value)), Stats.Value, false);
        hit = SpellHit.Resolve(plan, Stats.Value, Values(), ownWithQuality, conversion);
        Assert.Equal(new DamageRange(50, 50), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(150, 150), hit[DamageType.Fire]);
    }

    [Fact]
    public void Creation_and_refresh_inherited_weapon_values_do_not_become_spell_hit_inputs()
    {
        int effect = Skills.Value.EffectRow("Fireball");
        var plan = SkillHitPlan.ForSkill(Skills.Value, effect, 1);
        var own = Skills.Value.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var character = Values(("spell_damage_+%", 12), ("fire_damage_%_to_add_as_chaos", 20));
        Dictionary<string, int> Panel(int weaponDps, int legacySpellMax)
        {
            var final = new Dictionary<uint, int>(character);
            final[Stats.Value.Key("hundred_times_damage_per_second")!.Value] = weaponDps;
            final[Stats.Value.Key("spell_maximum_total_damage")!.Value] = legacySpellMax;
            var conversion = DamageStats.Conversion(character, Skills.Value.EffectStats(effect, 1), Stats.Value, false);
            var hit = SpellHit.Resolve(plan, Stats.Value, final, own, conversion, 2);
            return Rows(plan, hit, 133);
        }
        var created = Panel(1070, 99);
        var refreshed = Panel(99999, 99999);
        Assert.Equal(created.OrderBy(kv => kv.Key), refreshed.OrderBy(kv => kv.Key));
        Assert.Equal(12, created["spell_minimum_total_damage"]);
        Assert.Equal(18, created["spell_maximum_total_damage"]);
        Assert.Equal(2, created["spell_minimum_chaos_damage"]);
        Assert.Equal(3, created["spell_maximum_chaos_damage"]);
        Assert.Equal(1546, created["display_hundred_times_damage_per_skill_use"]);
    }

    [Theory]
    [InlineData("ScorchingRay")]
    [InlineData("RighteousFire")]
    [InlineData("Haste")]
    public void Real_hitless_spells_clear_inherited_weapon_display_even_with_added_spell_damage(string effect)
    {
        int row = Skills.Value.EffectRow(effect);
        var plan = SkillHitPlan.ForSkill(Skills.Value, row, 1);
        var inherited = Values(("hundred_times_damage_per_second", 1070),
            ("display_hundred_times_damage_per_skill_use", 714), ("spell_minimum_fire_damage", 99),
            ("spell_minimum_added_fire_damage", 100), ("spell_maximum_added_fire_damage", 200));
        var own = Skills.Value.EffectStats(row, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var hit = SpellHit.Resolve(plan, Stats.Value, inherited, own, new DamageConversion(), 2);
        Assert.Same(DamagePacket.Empty, hit);
        var rows = Rows(plan, hit, 200);
        Assert.Equal(14, rows.Count);
        foreach (var (name, value) in rows) inherited[Stats.Value.Key(name)!.Value] = value;
        foreach (string name in rows.Keys) Assert.Equal(0, inherited[Stats.Value.Key(name)!.Value]);
    }

    [Theory]
    [InlineData("PlayerMelee")]
    [InlineData("BurningArrow")]
    [InlineData("CausticArrow")]
    [InlineData("EnduringCry")]
    public void Attacks_and_nonspell_actions_keep_their_existing_display_path(string effect)
    {
        var plan = SkillHitPlan.ForSkill(Skills.Value, Skills.Value.EffectRow(effect), 1);
        Assert.NotEqual(SkillHitKind.Spell, plan.Kind);
        Assert.Empty(Rows(plan, DamagePacket.Of(DamageType.Fire, 100, 200), 200));
        Assert.Same(DamagePacket.Empty, SpellHit.Resolve(plan, Stats.Value, Values(), Values(), new DamageConversion()));
    }

    [Fact]
    public void Crit_stats_do_not_turn_the_precrit_display_into_an_expected_critical_hit()
    {
        var plan = SkillHitPlan.Create(false, true, DamagePacket.Of(DamageType.Fire, 10, 20));
        var hit = SpellHit.Resolve(plan, Stats.Value,
            Values(("spell_critical_strike_chance", 10000), ("spell_critical_strike_multiplier", 300)), Values(), new DamageConversion());
        Assert.Equal(1500, Rows(plan, hit)["display_hundred_times_damage_per_skill_use"]);
    }

    [Theory]
    [InlineData("Spell_TotalDamage", "total", 674, 675)]
    [InlineData("Spell_PhysicalDamage", "physical", 261, 262)]
    [InlineData("Spell_FireDamage", "fire", 255, 256)]
    [InlineData("Spell_ColdDamage", "cold", 253, 254)]
    [InlineData("Spell_LightningDamage", "lightning", 257, 259)]
    [InlineData("Spell_ChaosDamage", "chaos", 258, 260)]
    public void Native_spell_ranges_read_whole_integer_keys_with_the_spell_and_secondary_gates(
        string id, string type, uint minKey, uint maxKey)
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables", "CharacterPanelStats.json")));
        var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == id);
        Assert.Equal(minKey, Stats.Value.Key($"spell_minimum_{type}_damage"));
        Assert.Equal(maxKey, Stats.Value.Key($"spell_maximum_{type}_damage"));
        Assert.Equal(new[] { minKey - 1, maxKey - 1 }, row.GetProperty("StatsKeys1").EnumerateArray().Select(e => e.GetUInt32()));
        Assert.Contains(437, row.GetProperty("StatsKeys2").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Contains(1674, row.GetProperty("StatsKeys3").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Empty(row.GetProperty("__col8").EnumerateArray());
        Assert.Equal(2, row.GetProperty("CharacterPanelDescriptionModesKey").GetInt32());
    }

    [Fact]
    public void Native_damage_averages_and_casts_per_second_use_hundredths()
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables", "CharacterPanelStats.json")));
        foreach (var (id, key) in new[] { ("DPS", 679), ("DamagePerUse", 2044), ("CPS", 682) })
        {
            var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == id);
            Assert.Equal(key - 1, Assert.Single(row.GetProperty("StatsKeys1").EnumerateArray()).GetInt32());
            Assert.Equal(14, Assert.Single(row.GetProperty("__col8").EnumerateArray()).GetInt32());
        }
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}

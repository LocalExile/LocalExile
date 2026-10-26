using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class HarbingerCompanionCatalogTests
{
    [Theory]
    [InlineData(20105, 0, 5892, 12, 2)]
    [InlineData(60867, 1, 5893, 13, 1)]
    [InlineData(18859, 2, 5894, 14, 1)]
    public void Real_sources_resolve_the_current_variants_and_their_own_buff_skills(
        int node, int kind, int monsterRow, int alternate, int buffCount)
    {
        if (Data() is not { } data) return;
        var catalog = new HarbingerCompanionCatalog(data);
        var companion = catalog.ForNode((ushort)node);
        Assert.NotNull(companion);
        Assert.Null(catalog.Error);
        Assert.Equal((HarbingerCompanionCatalog.Kind)kind, companion.Type);
        Assert.Equal(monsterRow, companion.Monster.Row);
        Assert.Equal(alternate, companion.AlternateMinion);
        Assert.Equal(1, companion.Limit);
        Assert.Null(companion.CountStat); // These immortal varieties deliberately have no ActiveCountStat.
        Assert.Equal(1, companion.SummonLevel);
        Assert.Equal(buffCount, companion.Buffs.Count);
        Assert.Equal(kind == (int)HarbingerCompanionCatalog.Kind.Focus, companion.RequiresChannelling);
        Assert.True(companion.Monster.Supported);
        var stats = new StatCatalog(data);
        Assert.Equal(100, companion.MinionStats[Key(stats, "base_cooldown_speed_+%")]);
        Assert.All(companion.Buffs, b => Assert.True(b.CooldownNotRecoverDuringActive));
    }

    [Fact]
    public void Arcane_inputs_map_to_recipient_stats_without_inventing_shock_or_spell_damage()
    {
        if (Data() is not { } data) return;
        var stats = new StatCatalog(data);
        var companion = new HarbingerCompanionCatalog(data).ForNode(20105)!;
        var arcane = companion.Buffs.Single(b => b.EffectId == "HarbingerArcaneSurgeUber");
        Assert.Equal((ushort)895, arcane.Definition);
        Assert.Equal(1, arcane.BindingRow);
        Assert.Equal(700, arcane.CastMs);
        Assert.Equal(4000, arcane.DurationMs);
        Assert.Equal(8000, arcane.CooldownMs);
        Assert.Equal(20, arcane.RecipientStats[Key(stats, "arcane_surge_cast_speed_+%")]);
        Assert.Equal(58, arcane.RecipientStats[Key(stats, "mana_regeneration_rate_+%")]);
        Assert.Equal(0, arcane.RecipientStats[Key(stats, "spell_damage_+%_final_from_arcane_surge")]);
        Assert.DoesNotContain(Key(stats, "support_arcane_surge_cast_speed_+%"), arcane.RecipientStats.Keys);
        var lightning = companion.Buffs.Single(b => b.EffectId == "HarbingerArcaneSurgeUber2");
        Assert.Equal((ushort)929, lightning.Definition);
        Assert.Equal(31, lightning.BindingRow); // SkillID 32 is an explicit table key, not array index 32.
        Assert.Equal(10, lightning.RecipientStats[Key(stats, "base_reduce_enemy_lightning_resistance_%")]);
        Assert.Equal(0, lightning.RecipientStats[Key(stats, "base_chance_to_shock_%")]);
    }

    [Fact]
    public void Focus_constant_prefix_and_damage_key_mapping_preserve_its_final_reduction()
    {
        if (Data() is not { } data) return;
        var stats = new StatCatalog(data);
        var focus = Assert.Single(new HarbingerCompanionCatalog(data).ForNode(18859)!.Buffs);
        Assert.Equal((ushort)924, focus.Definition);
        Assert.Equal(28, focus.BindingRow);
        Assert.Equal(4000, focus.DurationMs);
        Assert.Equal(8000, focus.CooldownMs);
        foreach (string flag in new[] { "immune_to_status_ailments", "base_cannot_be_stunned", "unaffected_by_curses" })
            Assert.Equal(1, focus.RecipientStats[Key(stats, flag)]);
        Assert.Equal(-20, focus.SkillStats[Key(stats, "base_damage_taken_+%")]);
        Assert.DoesNotContain(Key(stats, "base_damage_taken_+%"), focus.RecipientStats.Keys);
        Assert.Equal(-20, focus.RecipientStats[Key(stats, "harbinger_channel_buff_damage_taken_+%_final")]);
    }

    [Fact]
    public void Synthetic_data_drives_timing_values_and_keyed_buff_lookup_instead_of_hardcoded_numbers()
    {
        using var data = new Fixture();
        var companion = new HarbingerCompanionCatalog(data.Folder).ForNode(60867)!;
        Assert.NotNull(companion);
        var buff = Assert.Single(companion.Buffs);
        Assert.Equal((ushort)0, buff.Definition);
        Assert.Equal(0, buff.BindingRow); // row zero still has explicit SkillID 31
        Assert.Equal(911, buff.CastMs);
        Assert.Equal(12345, buff.DurationMs);
        Assert.Equal(6789, buff.CooldownMs);
        Assert.Equal(-17, buff.RecipientStats[4]);
        Assert.Equal(100, companion.MinionStats[5]);
        Assert.Equal(6u, companion.CountStat);
    }

    [Theory]
    [InlineData(0, false, false)] // a standard/other alternate variant cannot replace this Phrecia variant
    [InlineData(13, true, false)] // an incomplete recipient definition must disable this companion
    [InlineData(13, false, true)] // a different buff cannot be selected merely because the numeric key exists
    public void Wrong_variants_partial_mappings_and_wrong_buff_identity_are_refused(int alternate, bool missingBuff, bool wrongBuff)
    {
        using var data = new Fixture(alternate, missingBuff, wrongBuff);
        Assert.Null(new HarbingerCompanionCatalog(data.Folder).ForNode(60867));
    }

    [Fact]
    public void Unknown_nodes_and_missing_tables_do_not_fabricate_a_companion()
    {
        var catalog = new HarbingerCompanionCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Null(catalog.ForNode(0));
        Assert.Null(catalog.ForNode(60867));
        Assert.Contains("missing", catalog.Error);
        if (Data() is not { } data) return;
        catalog = new(data);
        Assert.Equal(3, catalog.ForNodes([20105, 60867, 18859, 60867, 0]).Count());
    }

    static uint Key(StatCatalog stats, string name) => stats.Key(name)!.Value;

    static string? Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(data, "tables", "AddBuffToTargetVarieties.json"))
                && File.Exists(Path.Combine(data, "tables", "MonsterVarieties.json"))) return data;
        }
        return null;
    }

    sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("localexile-harbinger-catalog-").FullName;

        public Fixture(int alternate = 13, bool missingBuff = false, bool wrongBuff = false)
        {
            string tables = Directory.CreateDirectory(Path.Combine(Folder, "tables")).FullName;
            void Write(string name, object value) => File.WriteAllText(Path.Combine(tables, name + ".json"), JsonSerializer.Serialize(value));
            Write("Stats", new[] { "alternate_minion", "number_of_harbinger_time_ascendancy_minions_allowed",
                "base_skill_effect_duration", "action_speed_-%", "base_cooldown_speed_+%", "harbinger_count" }.Select(id => new { Id = id }));
            Write("PassiveSkills", new[] { new { PassiveSkillGraphId = 60867, GrantedEffectsPerLevel = 0 } });
            Write("GrantedEffects", new[]
            {
                new { Id = "SummonHarbingerOfTimeAscendancy", StatSet1 = 0, CastTime = 0, SkillID = 0, CooldownNotRecoverDuringActive = false },
                new { Id = "HarbingerWarp", StatSet1 = 1, CastTime = 500, SkillID = 0, CooldownNotRecoverDuringActive = false },
                new { Id = "HarbingerActionSpeedAuraUber", StatSet1 = 2, CastTime = 911, SkillID = 31, CooldownNotRecoverDuringActive = true },
            });
            Write("GrantedEffectsPerLevel", new[]
            {
                new { GrantedEffect = 0, Level = 1, Cooldown = 0, StoredUses = 0 },
                new { GrantedEffect = 1, Level = 1, Cooldown = 3000, StoredUses = 1 },
                new { GrantedEffect = 2, Level = 1, Cooldown = 6789, StoredUses = 1 },
            });
            Write("GrantedEffectStatSets", new[]
            {
                new { ImplicitStats = Array.Empty<int>(), ConstantStats = new[] { 0, 1 }, ConstantStatsValues = new[] { alternate, 1 } },
                new { ImplicitStats = Array.Empty<int>(), ConstantStats = Array.Empty<int>(), ConstantStatsValues = Array.Empty<int>() },
                new { ImplicitStats = Array.Empty<int>(), ConstantStats = new[] { 2, 3 }, ConstantStatsValues = new[] { 12345, -17 } },
            });
            Write("GrantedEffectStatSetsPerLevel", Enumerable.Range(0, 3).Select(i => new { StatSet = i, GemLevel = 1 }));
            Write("MinionType", new[] { new { Id = "HarbingerAscendancyTime", LimitStat = 1, ActiveCountStat = 5 } });
            if (missingBuff) Write("BuffDefinitions", Array.Empty<object>());
            else Write("BuffDefinitions", new[] { new { Id = wrongBuff ? "wrong_buff" : "harbinger_action_speed_aura_uber",
                StatsKeys = new[] { 3 }, IsRecovery = false, __col44 = false } });
            Write("AddBuffToTargetVarieties", new[] { new { BuffDefinitions = 0, __col1 = Array.Empty<int>(), StatsKeys = new[] { 3 }, __col5 = 31 } });
            Write("MonsterVarieties", new[] { new { Id = "Metadata/Monsters/Harbinger/HarbingerMinion14", Name = "Time Harbinger",
                BaseMonsterTypeIndex = "FixtureHarbinger", AOFiles = Array.Empty<string>(), LifeMultiplier = 100, MonsterTypesKey = 0,
                MovementSpeed = 45, ObjectSize = 0, MaxAgroRange = 0, GrantedEffectsKeys = new[] { 1, 2 }, AttackSpeed = 1500,
                MaximumAttackDistance = 0, DamageMultiplier = 100, ExperienceMultiplier = 0, ModsKeys = new[] { 0 } } });
            Write("Mods", new[] { new { StatsKey1 = 4, Stat1Min = 100, Stat1Max = 100 } });
            File.WriteAllText(Path.Combine(Folder, "object-types.json"), JsonSerializer.Serialize(new
            {
                types = new[] { new { type = "FixtureHarbinger", components = MonsterObject.BaseComponents.Append("DiesAfterTime"), read = 12 } }
            }));
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Folder);
            if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to clean an unexpected fixture directory");
            Directory.Delete(target, recursive: true);
        }
    }
}

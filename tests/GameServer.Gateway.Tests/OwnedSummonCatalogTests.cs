using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class OwnedSummonCatalogTests
{
    [Fact]
    public void Flesh_Puppets_modifies_all_three_gem_variants_and_does_not_grant_a_free_skill()
    {
        if (Data() is not { } data) return;
        var catalog = new OwnedSummonCatalog(data);
        var stats = new StatCatalog(data);
        var source = catalog.SourceForNode(18817);
        Assert.NotNull(source);
        Assert.Null(source.GrantedSkill);
        Assert.Null(catalog.ForNode(18817));
        Assert.Equal(10397u, source.GemLevelStat);
        Assert.Equal(5, source.Stats[Key(stats, "raise_zombie_gem_level_+")]);
        Assert.Equal(50, source.Stats[Key(stats, "zombie_scale_+%")]);
        Assert.Equal(new[] { 93, 94, 95 }, source.ModifiedGems.Select(g => g.GemEffectsRow));
        Assert.Equal(new[] { "RaiseZombie", "RaiseZombieAltX", "RaiseZombieAltY" }, source.ModifiedGems.Select(g => g.EffectId));
        foreach (var variant in source.ModifiedGems)
            Assert.Equal(5, catalog.GemLevelBonus(variant.Effect, name => name == "raise_zombie_gem_level_+" ? 5 : 1000));
        Assert.Equal(0, catalog.GemLevelBonus(new SkillCatalog(data).EffectRow("Fireball"), _ => 1000));
        Assert.Null(catalog.Error);
    }

    [Fact]
    public void Current_Zombie_variant_and_level_twenty_to_twenty_five_values_come_from_foreign_rows()
    {
        if (Data() is not { } data) return;
        var catalog = new OwnedSummonCatalog(data);
        var skills = new SkillCatalog(data);
        var stats = new StatCatalog(data);
        var zombie = catalog.ForEffect(skills.EffectRow("RaiseZombie"), 20)!;
        Assert.NotNull(zombie);
        Assert.Equal(2179, zombie.Monster.Row);
        Assert.Equal("Metadata/Monsters/RaisedZombies/RaisedZombieStandard", zombie.Monster.Id);
        Assert.Equal(375, zombie.Monster.LifeMultiplier);
        Assert.Equal(165, zombie.Monster.DamageMultiplier);
        Assert.Equal(1170, zombie.Monster.AttackMs);
        Assert.Equal(70, zombie.ActorLevel);
        Assert.Equal(OwnedSummonCatalog.ActorLevelSource.SkillStat, zombie.LevelSource);
        Assert.False(zombie.ActorLevelIsInferred);
        Assert.Equal(6, zombie.BaseLimit);
        Assert.Equal(727u, zombie.LimitStat);
        Assert.Equal(3529u, zombie.CountStat);
        Assert.Null(zombie.DurationMs); // infinite_minion_duration, not a guessed thirty-second wolf timer
        Assert.True(zombie.ManuallyCastable);
        Assert.True(zombie.RequiresCorpse);
        Assert.False(zombie.ConsumesNearbyCorpses);
        Assert.Equal(7647, catalog.LifeWithoutOwner(zombie)); // floor(floor(1478*3.75)*1.38)
        Assert.Equal((385, 897), catalog.BaseDamage(zombie)); // 332.05*1.65*1.17, spread +/-40%
        Assert.Equal(40, zombie.ModStats[Key(stats, "physical_damage_reduction_rating_+%")]);
        Assert.Equal(30, zombie.ModStats[Key(stats, "stun_threshold_+%")]);
        Assert.Equal(new[] { "Melee", "ZombieSlam", "GAZombieCorpseGroundImpact" }, zombie.DeclaredSkills.Select(s => s.EffectId));
        var slam = zombie.DeclaredSkills.Single(s => s.EffectId == "ZombieSlam");
        Assert.Equal(new[] { 1, 12, 20, 33, 45 }, slam.Levels.Select(l => l.ActorLevelRequirement));
        Assert.All(slam.Levels, l => Assert.Equal(6000, l.CooldownMs));
        var boosted = catalog.ForEffect(zombie.Effect, 25)!;
        Assert.Equal(80, boosted.ActorLevel);
        Assert.Equal(7, boosted.BaseLimit);
        Assert.Equal(13875, catalog.LifeWithoutOwner(boosted)); // 2500*3.75*1.48
        Assert.False(zombie.HasKnownAiSchedule);
        Assert.False(zombie.HasKnownWireOwner);
        Assert.False(zombie.HasKnownAtLimitPolicy);
    }

    [Fact]
    public void Slamming_and_Falling_preserve_different_corpse_and_life_rules()
    {
        if (Data() is not { } data) return;
        var catalog = new OwnedSummonCatalog(data);
        var skills = new SkillCatalog(data);
        var slam = catalog.ForEffect(skills.EffectRow("RaiseZombieAltX"), 25)!;
        Assert.Equal(1, slam.AlternateGemIndex);
        Assert.True(slam.RequiresCorpse);
        Assert.False(slam.DiesOnImpact);
        Assert.Equal(9375, catalog.LifeWithoutOwner(slam)); // This variant has no regular gem's +48% more life stat.
        var falling = catalog.ForEffect(skills.EffectRow("RaiseZombieAltY"), 20)!;
        Assert.Equal(2, falling.AlternateGemIndex);
        Assert.Equal(200, falling.BaseLimit);
        Assert.False(falling.RequiresCorpse);
        Assert.True(falling.DiesOnImpact);
        Assert.Equal(slam.Monster.Row, falling.Monster.Row); // Alternate gem index is not a MonsterVarieties row.
    }

    [Fact]
    public void Phrecia_spiders_are_level_eighty_four_and_have_their_own_poison_skill_buff_and_count_keys()
    {
        if (Data() is not { } data) return;
        var catalog = new OwnedSummonCatalog(data);
        var stats = new StatCatalog(data);
        var source = catalog.SourceForNode(22622)!;
        Assert.Equal(new OwnedSummonCatalog.Grant(47614, 6454, "RaiseSpiders", 20), source.GrantedSkill);
        Assert.Equal(200, source.Stats[Key(stats, "local_display_raise_spider_on_kill_%_chance")]);
        var spider = catalog.ForNode(22622)!;
        Assert.NotNull(spider);
        Assert.Equal(5624, spider.Monster.Row);
        Assert.Equal("Metadata/Monsters/SummonedSpider/SummonedSpider", spider.Monster.Id);
        Assert.Equal(84, spider.ActorLevel); // GrantedEffectsPerLevel.PlayerLevelReq fallback, not Fang's level 1/75.
        Assert.Equal(OwnedSummonCatalog.ActorLevelSource.GrantedEffectLevelRequirement, spider.LevelSource);
        Assert.True(spider.ActorLevelIsInferred);
        Assert.Equal(75, catalog.ForEffect(spider.Effect, 1)!.ActorLevel);
        Assert.Equal(180, spider.Monster.LifeMultiplier);
        Assert.Equal(176, spider.Monster.DamageMultiplier);
        Assert.Equal(960, spider.Monster.AttackMs);
        Assert.Equal(1, catalog.LifeWithoutOwner(spider)); // SummonedSpider.ot overrides its table-derived 5533.
        Assert.Equal((942, 1413), catalog.BaseDamage(spider)); // 696.95*1.76*.96, spread +/-20%
        Assert.Equal(20, spider.BaseLimit);
        Assert.Equal(5404u, spider.LimitInputStat);
        Assert.Equal(21348u, spider.LimitStat);
        Assert.Equal(5405u, spider.CountStat);
        Assert.Equal(30000, spider.DurationMs);
        Assert.Equal(2000, spider.CooldownMs);
        Assert.False(spider.ManuallyCastable);
        Assert.False(spider.RequiresCorpse); // It consumes nearby corpses on its on-kill trigger instead of a targeted cast.
        Assert.True(spider.ConsumesNearbyCorpses);
        Assert.Equal(2, spider.MinionsPerCorpse);
        Assert.Equal(100, spider.TriggerChanceOnKill); // The actual skill stat, distinct from the node's display 200.
        var viper = Assert.Single(spider.DeclaredSkills);
        Assert.Equal("SummonedSpiderViperStrike", viper.EffectId);
        Assert.Equal(100, viper.LevelOneStats[Key(stats, "base_chance_to_poison_on_hit_%")]);
        Assert.Equal(4000, viper.LevelOneStats[Key(stats, "base_skill_effect_duration")]);
        Assert.Equal(1, viper.LevelOneStats[Key(stats, "poison_duration_is_skill_duration")]);
        Assert.Equal(1, spider.ModStats[Key(stats, "phase_through_objects")]);
        Assert.Equal(1, spider.TemplateStats[Key(stats, "base_cannot_be_damaged")]);
        Assert.Equal(1, spider.TemplateStats[Key(stats, "untargetable_by_monster_ai")]);
        var buff = spider.Buff!;
        Assert.Equal((ushort)1236, buff.Definition);
        Assert.Equal("summoned_spider_buff", buff.Id);
        Assert.Equal(2, buff.PerMinionStats[Key(stats, "attack_speed_+%")]);
        Assert.Equal(15, buff.PerMinionStats[Key(stats, "base_poison_damage_+%")]);
        Assert.False(buff.IsRecovery);
        Assert.False(buff.ExtraU16);
        Assert.False(buff.ShowCount);
        Assert.Equal(2, buff.StackMode);
        Assert.False(spider.HasKnownAtLimitPolicy); // Never borrow SpectralWolf's special at-cap refresh rule.
    }

    [Fact]
    public void Pinned_corpse_rule_and_template_constants_match_the_available_raw_client_sources()
    {
        if (Data() is not { } data) return;
        using var active = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", "ActiveSkills.json")));
        string description = active.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == "raise_spiders")
            .GetProperty("Description").GetString()!;
        Assert.Contains("Raise two spectral spiders from each nearby corpse", description);
        var catalog = new OwnedSummonCatalog(data);
        var stats = new StatCatalog(data);
        foreach (var summon in new[] { catalog.ForNode(22622)!, catalog.ForEffect(new SkillCatalog(data).EffectRow("RaiseZombie"), 20)! })
        {
            string raw = Path.Combine(Path.GetDirectoryName(data)!, "Tools", "re", "out", "ot",
                summon.TemplateStatsSource.Replace('/', '_'));
            if (!File.Exists(raw)) continue; // OT cache is an optional RE tool, never required in a fresh clone.
            string text = File.ReadAllText(raw);
            foreach (var (key, value) in summon.TemplateStats)
                Assert.Matches(@"\b(?:set_)?" + System.Text.RegularExpressions.Regex.Escape(stats.Name(key))
                    + @"\s*=\s*" + value + @"\b", text);
        }
    }

    [Fact]
    public void Synthetic_foreign_keys_and_values_drive_the_catalog_instead_of_array_positions_or_constants()
    {
        using var fixture = new Fixture();
        var catalog = new OwnedSummonCatalog(fixture.Folder);
        var stats = new StatCatalog(fixture.Folder);
        var source = catalog.SourceForNode(18817)!;
        Assert.Equal(new[] { 1, 2, 0 }, source.ModifiedGems.Select(g => g.GemEffectsRow));
        Assert.Equal(new[] { 0, 1, 2 }, source.ModifiedGems.Select(g => g.Effect));
        var zombie = catalog.ForEffect(0, 20)!;
        Assert.NotNull(zombie);
        Assert.Equal(1, zombie.Monster.Row); // Display row has Id 51; it is not an array index.
        Assert.Equal(60, zombie.ActorLevel); // Explicit stat wins over GEPL's requirement 40.
        Assert.Equal(3, zombie.BaseLimit);
        Assert.Equal(437, catalog.LifeWithoutOwner(zombie));
        Assert.Equal((48, 72), catalog.BaseDamage(zombie));
        Assert.Equal(37, zombie.ModStats[Key(stats, "stun_threshold_+%")]);
        var spider = catalog.ForNode(22622)!;
        Assert.Equal(88, spider.ActorLevel);
        Assert.Equal(12345, spider.DurationMs);
        Assert.Equal(777, spider.CooldownMs);
        Assert.Equal(7, spider.BaseLimit);
        Assert.Equal(73, spider.TriggerChanceOnKill);
        Assert.Equal(9, spider.Buff!.PerMinionStats[Key(stats, "attack_speed_+%")]);
        Assert.Equal(17, spider.Buff.PerMinionStats[Key(stats, "base_poison_damage_+%")]);
        Assert.Null(catalog.ForEffect(4, 1)); // A declared monster melee skill is not a player summon.
        Assert.Null(catalog.Error);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Broken_grants_variable_intrinsics_and_wrong_buff_keys_are_not_silently_replaced(
        bool wrongGrant, bool variableMod, bool wrongBuff)
    {
        using var fixture = new Fixture(wrongGrant, variableMod, wrongBuff);
        var catalog = new OwnedSummonCatalog(fixture.Folder);
        Assert.Null(catalog.ForNode(22622));
        if (!wrongBuff) Assert.Contains("invalid", catalog.Error);
    }

    static uint Key(StatCatalog stats, string name) => stats.Key(name)!.Value;
    static string? Data()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string folder = Path.Combine(directory.FullName, "data");
            if (File.Exists(Path.Combine(folder, "tables", "MinionType.json"))
                && File.Exists(Path.Combine(folder, "tables", "GemEffects.json"))
                && File.Exists(Path.Combine(folder, "object-types.json"))) return folder;
        }
        return null;
    }

    sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("localexile-owned-summon-catalog-").FullName;
        public Fixture(bool wrongGrant = false, bool variableMod = false, bool wrongBuff = false)
        {
            string tables = Directory.CreateDirectory(Path.Combine(Folder, "tables")).FullName;
            void Write(string name, object value) => File.WriteAllText(Path.Combine(tables, name + ".json"), JsonSerializer.Serialize(value));
            string[] names = ["raise_zombie_gem_level_+", "zombie_scale_+%", "local_display_raise_spider_on_kill_%_chance",
                "display_minion_monster_type", "base_display_minion_actor_level", "base_number_of_zombies_allowed",
                "infinite_minion_duration", "active_skill_minion_life_+%_final", "active_skill_minion_from_alternate_gem_index",
                "raise_zombie_does_not_use_corpses", "number_of_spider_minions_allowed", "base_skill_effect_duration",
                "chance_to_cast_on_kill_%_target_self", "summoned_spider_grants_attack_speed_+%", "summoned_spider_grants_poison_damage_+%",
                "number_of_zombies_allowed", "number_of_active_zombies", "virtual_number_of_spider_minions_allowed",
                "number_of_active_spider_minions", "attack_speed_+%", "base_poison_damage_+%", "stun_threshold_+%",
                "phase_through_objects", "item_drop_slots", "monster_no_drops_or_experience", "untargetable_by_monster_ai",
                "is_spider", "base_cannot_be_damaged", "cannot_be_chained_from", "maximum_life_is_one"];
            int Row(string name) => Array.IndexOf(names, name);
            Write("Stats", names.Select(id => new { Id = id }));
            Write("PassiveSkills", new object[]
            {
                new { PassiveSkillGraphId = 18817, Stats = new[] { 0, 1 }, Stat1Value = 5, Stat2Value = 50, GrantedEffectsPerLevel = (int?)null },
                new { PassiveSkillGraphId = 22622, Stats = new[] { 2 }, Stat1Value = 200, GrantedEffectsPerLevel = wrongGrant ? 0 : 3 },
            });
            Write("SkillGems", new[] { new { MinionGlobalSkillLevelStat = 0, GemVariants = new[] { 1, 2, 0 } } });
            Write("GemEffects", new[] { new { GrantedEffect = 2 }, new { GrantedEffect = 0 }, new { GrantedEffect = 1 } });
            string[] ids = ["RaiseZombie", "RaiseZombieAltX", "RaiseZombieAltY", "RaiseSpiders", "Melee", "ZombieSlam", "SummonedSpiderViperStrike"];
            Write("GrantedEffects", ids.Select((id, i) => new { Id = id, StatSet1 = i, StatSet2 = Array.Empty<int>(), CastTime = 850, ActiveSkill = i == 3 ? 1 : 0 }));
            Write("ActiveSkills", new[] { new { IsManuallyCasted = true }, new { IsManuallyCasted = false } });
            Write("GrantedEffectsPerLevel", ids.Select((_, i) => new { GrantedEffect = i, Level = i <= 3 ? 20 : 1,
                PlayerLevelReq = i == 3 ? 88 : 40, Cooldown = i == 3 ? 777 : 0 }));
            object Set(params (string Name, int Value)[] values) => new { ImplicitStats = Array.Empty<int>(),
                ConstantStats = values.Select(v => Row(v.Name)), ConstantStatsValues = values.Select(v => v.Value) };
            Write("GrantedEffectStatSets", new[]
            {
                Set(("display_minion_monster_type", 51), ("base_display_minion_actor_level", 60), ("base_number_of_zombies_allowed", 3), ("infinite_minion_duration", 1), ("active_skill_minion_life_+%_final", 25)),
                Set(("display_minion_monster_type", 51), ("base_display_minion_actor_level", 60), ("base_number_of_zombies_allowed", 3), ("infinite_minion_duration", 1), ("active_skill_minion_from_alternate_gem_index", 1)),
                Set(("display_minion_monster_type", 51), ("base_display_minion_actor_level", 60), ("base_number_of_zombies_allowed", 200), ("infinite_minion_duration", 1), ("raise_zombie_does_not_use_corpses", 1), ("active_skill_minion_from_alternate_gem_index", 2)),
                Set(("number_of_spider_minions_allowed", 7), ("base_skill_effect_duration", 12345), ("chance_to_cast_on_kill_%_target_self", 73), ("summoned_spider_grants_attack_speed_+%", 9), ("summoned_spider_grants_poison_damage_+%", 17)),
                Set(), Set(), Set(),
            });
            Write("GrantedEffectStatSetsPerLevel", ids.Select((_, i) => new { StatSet = i, GemLevel = i <= 3 ? 20 : 1 }));
            Write("DisplayMinionMonsterType", new[] { new { Id = 51, MonsterVarietiesKey = 1 } });
            const string spider = "Metadata/Monsters/SummonedSpider/SummonedSpider", zombie = "Metadata/Monsters/RaisedZombies/RaisedZombieStandard";
            Write("MonsterVarieties", new[] { spider, zombie }.Select((id, i) => new { Id = id, Name = id, BaseMonsterTypeIndex = id,
                AOFiles = Array.Empty<string>(), LifeMultiplier = 350, MonsterTypesKey = 0, MovementSpeed = 30, ObjectSize = 2,
                MaxAgroRange = 45, GrantedEffectsKeys = i == 0 ? new[] { 6 } : new[] { 4, 5 }, AttackSpeed = 1500,
                MaximumAttackDistance = 11, DamageMultiplier = 200, ExperienceMultiplier = 0,
                ModsKeys = new[] { i }, AISFile = "server-only.ais" }));
            Write("Mods", new object[]
            {
                new { StatsKey1 = Row("phase_through_objects"), Stat1Min = 1, Stat1Max = variableMod ? 2 : 1 },
                new { StatsKey1 = Row("stun_threshold_+%"), Stat1Min = 37, Stat1Max = 37 },
            });
            Write("MinionType", new[]
            {
                new { Id = "ZombieMinion", LimitStat = Row("number_of_zombies_allowed"), ActiveCountStat = Row("number_of_active_zombies") },
                new { Id = "SpiderMinion", LimitStat = Row("virtual_number_of_spider_minions_allowed"), ActiveCountStat = Row("number_of_active_spider_minions") },
            });
            Write("BuffDefinitions", new[] { new { Id = "summoned_spider_buff", StatsKeys = new[] { Row("attack_speed_+%"), Row(wrongBuff ? "zombie_scale_+%" : "base_poison_damage_+%") }, IsRecovery = false, __col44 = false, ShowCount = false, UIStackMode = 2 } });
            Write("MonsterTypes", new[] { new { IsPlayerMinion = true, AltLife1 = false, AltLife2 = false, DamageSpread = 20, BaseDamageIgnoresAttackSpeed = false } });
            Write("DefaultMonsterStats", Enumerable.Range(0, 100).Select(_ => new { MonsterLife = 1000, MinionLife = 100, AltLife1 = 1000, AltLife2 = 1000,
                Damage = 999, MinionDamage = 20, Experience = 0, Accuracy = 0, Evasion = 0, EvasiveEvasion = 0 }));
            var components = MonsterObject.BaseComponents.Append("DiesAfterTime").ToArray();
            File.WriteAllText(Path.Combine(Folder, "object-types.json"), JsonSerializer.Serialize(new { types = new[] { spider, zombie }
                .Select(id => new { type = id, components, read = components.Length }) }));
        }
        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}

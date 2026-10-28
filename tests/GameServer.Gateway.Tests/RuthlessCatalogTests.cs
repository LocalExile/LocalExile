using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RuthlessCatalogTests
{
    sealed class CatalogData : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "localexile-mode-catalog-" + Guid.NewGuid().ToString("N"));

        public CatalogData()
        {
            Directory.CreateDirectory(Path.Combine(Folder, "tables"));
            Write("Stats", new[] { new { Id = "stat_regular", IsLocal = false }, new { Id = "stat_alternate", IsLocal = true } });
            Write("Tags", new[] { new { Id = "default" } });
        }

        public void Write(string name, object rows) =>
            File.WriteAllText(Path.Combine(Folder, "tables", name + ".json"), JsonSerializer.Serialize(rows));

        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }

    static object Node(int graph, bool alternate, int[] hardmodeStats, string hardmodeName = "") => new
    {
        PassiveSkillGraphId = graph, Id = "Node" + graph, Name = "Regular name", Stats = new[] { 0 },
        Stat1Value = 10, Stat2Value = 0, Stat3Value = 0, Stat4Value = 0, Stat5Value = 0,
        Characters = Array.Empty<int>(), SkillPointsGranted = 2, AscendancyKey = (int?)null,
        MasteryGroup = (int?)null, IsExpansion = false, IsMultipleChoice = false,
        HasHardmodeAlternate = alternate, NameHardmode = hardmodeName, StatsHardmode = hardmodeStats,
        Stat1ValueHardmode = 0, Stat2ValueHardmode = 0, Stat3ValueHardmode = 0, Stat4ValueHardmode = 0, Stat5ValueHardmode = 0,
        SkillPointsGrantedHardmode = 0,
    };

    static object Mod(string id, int hash, bool alternate) => new
    {
        Id = id, HASH16 = hash, Name = id, Domain = 1, GenerationType = 1, Level = 1,
        StatsKey1 = 1, StatsKey2 = (int?)null, Stat1Min = 5, Stat1Max = 10,
        HasHardmodeStatValues = alternate, Stat1MinHardmode = 0, Stat1MaxHardmode = 0,
        Families = new[] { hash }, SpawnWeight_TagsKeys = new[] { 0 }, SpawnWeight_Values = new[] { 1000 },
        GenerationWeight_TagsKeys = Array.Empty<int>(), GenerationWeight_Values = Array.Empty<int>(),
        IsEssenceOnlyModifier = false,
    };

    [Fact]
    public void Simultaneous_trees_use_their_own_stat_vectors_points_and_names()
    {
        using var data = new CatalogData();
        data.Write("PassiveSkills", new[] { Node(1, true, [1], "Ruthless name") });
        var regular = new PassiveTree(data.Folder);
        var ruthless = new PassiveTree(data.Folder, ruthless: true);

        Assert.Equal(new Dictionary<uint, int> { [1] = 10 }, regular.Stats([1]));
        Assert.Equal(new Dictionary<uint, int> { [2] = 0 }, ruthless.Stats([1]));
        Assert.Equal(2, regular.PointsGranted([1]));
        Assert.Equal(0, ruthless.PointsGranted([1]));
        Assert.Equal("Regular name", regular.Find(1)!.Name);
        Assert.Equal("Ruthless name", ruthless.Find(1)!.Name);
        // Loading the other mode must not modify a previously read regular catalog.
        Assert.Equal(10, regular.Stats([1])[1]);
    }

    [Fact]
    public void Empty_alternate_removes_regular_stats_but_an_unmarked_node_keeps_them()
    {
        using var data = new CatalogData();
        data.Write("PassiveSkills", new[] { Node(1, true, []), Node(2, false, [1], "Unused alternate") });
        var ruthless = new PassiveTree(data.Folder, ruthless: true);

        Assert.Empty(ruthless.Stats([1]));
        Assert.Equal("Regular name", ruthless.Find(1)!.Name);
        Assert.Equal(new Dictionary<uint, int> { [1] = 10 }, ruthless.Stats([2]));
        Assert.Equal(2, ruthless.PointsGranted([2]));
        Assert.Equal("Regular name", ruthless.Find(2)!.Name);
    }

    [Fact]
    public void Mod_alternates_keep_wire_identity_and_stat_locality_and_roll_explicit_zero()
    {
        using var data = new CatalogData();
        data.Write("Mods", new[] { Mod("Changed", 23, true), Mod("Regular", 24, false) });
        var regular = new ModCatalog(data.Folder);
        var ruthless = new ModCatalog(data.Folder, ruthless: true);
        var normal = regular.ById("Changed")!;
        var changed = ruthless.ById("Changed")!;

        Assert.Equal(normal.Hash, changed.Hash);
        Assert.Equal(normal.Row, changed.Row);
        Assert.Equal("stat_alternate", Assert.Single(changed.Stats).Id);
        Assert.True(changed.Stats[0].Local);
        Assert.Equal([0], changed.Roll(ModSection.Explicit, new Random(1)).Values);
        Assert.InRange(normal.Roll(ModSection.Explicit, new Random(1)).Values.Single(), 5, 10);
        Assert.Equal(normal.Stats, ruthless.ById("Regular")!.Stats);
        Assert.Equal(normal.Stats, regular.ById("Changed")!.Stats);
    }

    static string Data()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Extracted_unstoppable_loses_movement_speed_in_its_ruthless_alternate()
    {
        var regular = new PassiveTree(Data());
        var ruthless = new PassiveTree(Data(), ruthless: true);
        Assert.Contains(regular.Find(5819)!.Stats, stat => stat.Key == 180 && stat.Value == 10);
        Assert.DoesNotContain(ruthless.Find(5819)!.Stats, stat => stat.Key == 180);
        Assert.Equal(4, regular.Find(5819)!.Stats.Count);
        Assert.Equal(2, ruthless.Find(5819)!.Stats.Count);
    }

    [Fact]
    public void Extracted_boss_heal_and_loot_mods_use_the_ruthless_ranges()
    {
        var regular = new ModCatalog(Data());
        var ruthless = new ModCatalog(Data(), ruthless: true);
        Assert.Equal(0, Assert.Single(regular.ById("BossHealOnPlayerDeath")!.Stats).Min);
        Assert.Equal(40, Assert.Single(ruthless.ById("BossHealOnPlayerDeath")!.Stats).Min);
        Assert.Contains(regular.ById("MonsterMinotaurBoss")!.Stats,
            stat => stat.Id == "monster_dropped_item_rarity_+%" && stat.Min == 2000);
        Assert.Contains(ruthless.ById("MonsterMinotaurBoss")!.Stats,
            stat => stat.Id == "monster_dropped_item_rarity_+%" && stat.Min == 0);
    }

    [Theory]
    [InlineData("Frostblink", "FrostblinkHardMode")]
    [InlineData("Grace", "GraceHardMode")]
    [InlineData("Determination", "DeterminationHardMode")]
    [InlineData("Fireball", "Fireball")]
    public void Extracted_gems_use_the_mode_effect_without_changing_item_identity_or_experience(string name, string expected)
    {
        var regular = new ItemCatalog(Data());
        var ruthless = new ItemCatalog(Data(), ruthless: true);
        var skills = new SkillCatalog(Data());
        var type = regular.FindGem(name)!.Value.Type;
        var item = ItemCatalog.Create(type);

        Assert.Equal(name, skills.EffectId(regular.Gem(type)!.Variant(0).Effect));
        Assert.Equal(expected, skills.EffectId(ruthless.Gem(type)!.Variant(0).Effect));
        Assert.Equal(type.Hash, ruthless.Row(type.Row)!.Hash);
        Assert.Equal(regular.Gem(type)!.Thresholds, ruthless.Gem(type)!.Thresholds);
        Assert.Equal(expected, skills.EffectId(Assert.Single(ruthless.GemEffects(item))));
    }

    [Fact]
    public void Vaal_grace_keeps_its_vaal_skill_and_selects_the_ruthless_base_skill()
    {
        var regular = new ItemCatalog(Data());
        var ruthless = new ItemCatalog(Data(), ruthless: true);
        var skills = new SkillCatalog(Data());
        var type = regular.FindGem("Vaal Grace")!.Value.Type;
        var item = ItemCatalog.Create(type);

        Assert.Equal(new[] { "VaalGrace", "Grace" }, regular.GemEffects(item).Select(skills.EffectId));
        Assert.Equal(new[] { "VaalGrace", "GraceHardMode" }, ruthless.GemEffects(item).Select(skills.EffectId));
        Assert.True(ruthless.Gem(type)!.Variant(0).Creatable);
    }

    [Fact]
    public void Mode_name_lookup_and_gem_lookup_reach_the_same_frostblink_cooldown()
    {
        var regular = new SkillCatalog(Data());
        var ruthless = new SkillCatalog(Data(), ruthless: true);
        int normal = regular.EffectRow("Frostblink"), alternate = ruthless.EffectRow("Frostblink");

        Assert.Equal("Frostblink", regular.EffectId(normal));
        Assert.Equal("FrostblinkHardMode", ruthless.EffectId(alternate));
        Assert.Equal(3000, regular.CooldownMs(normal, 1));
        Assert.Equal(8000, ruthless.CooldownMs(alternate, 1));
        Assert.Equal(8000u, ruthless.Find(0x103, "Frostblink", 1)!.CooldownMs);
        Assert.True(InstantLocationSkill.Supported(ruthless, alternate));
    }

    [Fact]
    public void Null_or_missing_gem_alternate_falls_back_but_row_zero_is_a_real_alternate()
    {
        using var data = new CatalogData();
        data.Write("BaseItemTypes", new[] { new { Name = "Zero" }, new { Name = "Null" }, new { Name = "Missing" } });
        data.Write("ItemExperienceTypes", new[] { new { Id = "GemProgression1" } });
        data.Write("ItemExperiencePerLevel", new[] { new { ItemExperienceType = 0, ItemCurrentLevel = 1, Experience = 0 } });
        data.Write("SkillGems", Enumerable.Range(0, 3).Select(i => new
        {
            BaseItemTypesKey = i, GemVariants = new[] { i }, IsVaalVariant = false, IsSupport = false,
            ItemExperienceType = 0, RegularVariant = (int?)null,
        }));
        data.Write("GemEffects", new object[]
        {
            new { Name = "Zero", GrantedEffect = 10, GrantedEffectHardmode = (int?)0, GrantedEffect2 = (int?)null },
            new { Name = "Null", GrantedEffect = 11, GrantedEffectHardmode = (int?)null, GrantedEffect2 = (int?)null },
            new { Name = "Missing", GrantedEffect = 12, GrantedEffect2 = (int?)null },
        });
        var regular = new ItemCatalog(data.Folder);
        var ruthless = new ItemCatalog(data.Folder, ruthless: true);
        ItemCatalog.BaseType Type(int row) => new(row, "Gem" + row, "Gem" + row, (uint)row, 1, 1, 0, [], 0);

        Assert.Equal(10, regular.Gem(Type(0))!.Variant(0).Effect);
        Assert.Equal(0, ruthless.Gem(Type(0))!.Variant(0).Effect);
        Assert.Equal(11, ruthless.Gem(Type(1))!.Variant(0).Effect);
        Assert.Equal(12, ruthless.Gem(Type(2))!.Variant(0).Effect);
    }

    [Fact]
    public void Utility_flask_duration_and_onslaught_use_extracted_mode_values()
    {
        var regular = new ItemCatalog(Data());
        var ruthless = new ItemCatalog(Data(), ruthless: true);
        var quicksilver = regular.Find("Quicksilver Flask")!;
        Assert.Equal(6, regular.UtilityFlaskOf(quicksilver)!.Seconds);
        Assert.Equal(2, ruthless.UtilityFlaskOf(quicksilver)!.Seconds);
        var silver = regular.Find("Silver Flask")!;
        Assert.Contains(regular.UtilityFlaskOf(silver)!.Extra!.Stats,
            stat => stat.Stat == "base_movement_velocity_+%" && stat.Value == 20);
        Assert.Contains(ruthless.UtilityFlaskOf(silver)!.Extra!.Stats,
            stat => stat.Stat == "base_movement_velocity_+%" && stat.Value == 0);
        Assert.Equal(regular.FlaskOf(regular.Find("Small Life Flask")!), ruthless.FlaskOf(ruthless.Find("Small Life Flask")!));
    }

    [Fact]
    public void Explicit_zero_alternate_flask_stats_and_duration_do_not_fall_back()
    {
        using var data = new CatalogData();
        data.Write("BuffDefinitions", new[] { new { Id = "flask_test", StatsKeys = new[] { 0 }, GrantedFlags = Array.Empty<int>() } });
        data.Write("BuffTemplates", Array.Empty<object>());
        data.Write("ComponentCharges", new[] { new { BaseItemTypesKey = "TestFlask", MaxCharges = 60, PerCharge = 30 } });
        data.Write("Flasks", new[] { new
        {
            BaseItemTypesKey = 0, BuffDefinitionsKey = 0, LifePerUse = 0, ManaPerUse = 0,
            RecoveryTime = 60, RecoveryTime2 = 0, BuffStatValues = new[] { 40 }, BuffStatValues2 = new[] { 0 },
        } });
        ItemCatalog.BaseType[] types = [new(0, "TestFlask", "Test Flask", 1, 1, 1, 0, [], 60)];
        var regular = UtilityFlasks.Load(data.Folder, types)[0];
        var ruthless = UtilityFlasks.Load(data.Folder, types, ruthless: true)[0];

        Assert.Equal(6, regular.Seconds);
        Assert.Equal(0, ruthless.Seconds);
        Assert.Equal(40, Assert.Single(regular.Stats).Value);
        Assert.Equal(0, Assert.Single(ruthless.Stats).Value);
    }

    [Theory]
    [InlineData("Grace", "GraceHardMode", 143)]
    [InlineData("Determination", "DeterminationHardMode", 142)]
    public void Ruthless_auras_keep_native_buff_art_and_restore_using_the_available_mode(string normal, string alternate, ushort buff)
    {
        var skills = new SkillCatalog(Data(), ruthless: true);
        var stats = new StatCatalog(Data());
        var aura = new AuraCatalog.Aura(0x103, skills.EffectRow(normal), alternate, normal, 1, buff, 0, 5000);
        var pools = CharacterStats.For(skills.ClassFor(0), 1);
        Assert.True(AuraCatalog.Supported(alternate));
        Assert.Equal(buff, AuraCatalog.BuffRow(alternate));
        Assert.Equal(aura, Assert.Single(AuraCatalog.Restore([normal, alternate], [aura], pools)));
        var bonus = AuraCatalog.Bonus([aura], 0, skills, stats);
        // Native Ruthless auras retain the more multiplier and deliberately remove the ordinary flat defence.
        Assert.Equal(0, normal == "Grace" ? bonus.Evasion : bonus.Armour);
        Assert.Equal(40, normal == "Grace" ? bonus.EvasionMorePct : bonus.ArmourMorePct);
    }
}

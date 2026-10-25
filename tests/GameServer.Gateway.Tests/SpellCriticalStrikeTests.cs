using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SpellCriticalStrikeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static Func<string, int> Values(params (string Key, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Key == name).Value;

    [Theory]
    [InlineData("Fireball", 500)]
    [InlineData("FreezingPulse", 750)]
    [InlineData("IceNova", 750)]
    [InlineData("Arc", 600)]
    [InlineData("ScorchingRay", 0)]
    public void Each_spell_uses_its_own_extracted_base(string effect, int expected)
    {
        var skills = new SkillCatalog(Data());
        int row = skills.EffectRow(effect);
        Assert.Equal(expected, skills.SpellCritChance(row, 1));
        Assert.Equal(expected, skills.SpellCritChance(row, 20));
        Assert.Equal(expected, SpellCriticalStrike.Chance(skills.SpellCritChance(row, 1), Values()));
    }

    [Theory]
    [InlineData("Fireball", true)]
    [InlineData("FreezingPulse", true)]
    [InlineData("IceNova", true)]
    [InlineData("ScorchingRay", true)]
    [InlineData("Haste", true)]
    [InlineData("PlayerMelee", false)]
    [InlineData("EnduringCry", false)]
    public void Panel_spell_gating_comes_from_the_skill_type_not_the_absence_of_attack(string effect, bool expected)
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        int row = skills.EffectRow(effect);
        Assert.Equal(expected, skills.IsSpell(row));
        Assert.Contains((stats.Key("casting_spell")!.Value, expected ? 1 : 0), skills.EffectStats(row, 1));
    }

    [Fact]
    public void Catalog_selects_the_level_and_distinguishes_zero_missing_and_invalid_columns()
    {
        var dir = Directory.CreateTempSubdirectory("localexile-spellcrit-");
        try
        {
            string tables = Path.Combine(dir.FullName, "tables");
            Directory.CreateDirectory(tables);
            File.WriteAllText(Path.Combine(tables, "GrantedEffects.json"), """[{"Id":"TestSpell","StatSet1":0}]""");
            File.WriteAllText(Path.Combine(tables, "GrantedEffectsPerLevel.json"), "[]");
            File.WriteAllText(Path.Combine(tables, "GrantedEffectStatSetsPerLevel.json"), """
                [{"StatSet":0,"GemLevel":1,"SpellCritChance":500},
                 {"StatSet":0,"GemLevel":3,"SpellCritChance":750},
                 {"StatSet":0,"GemLevel":5,"SpellCritChance":0},
                 {"StatSet":0,"GemLevel":7},
                 {"StatSet":0,"GemLevel":8,"SpellCritChance":null},
                 {"StatSet":0,"GemLevel":9,"SpellCritChance":-1}]
                """);
            var skills = new SkillCatalog(dir.FullName);
            Assert.Equal(500, skills.SpellCritChance(0, 2));
            Assert.Equal(750, skills.SpellCritChance(0, 4));
            Assert.Equal(0, skills.SpellCritChance(0, 6));
            Assert.Null(skills.SpellCritChance(0, 7));
            Assert.Null(skills.SpellCritChance(0, 8));
            Assert.Null(skills.SpellCritChance(0, 9));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Player_and_skill_increases_add_once_and_ignore_the_old_final_display_chance()
    {
        var player = Values(("critical_strike_chance_+%", 20), ("spell_critical_strike_chance_+%", 30),
            ("spell_critical_strike_chance", 9999));
        var own = Values(("critical_strike_chance_+%", 40), ("spell_critical_strike_chance_+%", 10));
        Assert.Equal(1500, SpellCriticalStrike.Chance(750, player, own));
        Assert.Equal(1000, SpellCriticalStrike.Chance(500, player, own));
    }

    [Fact]
    public void Runtime_stat_vectors_keep_player_and_skill_inputs_separate()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var player = new Dictionary<uint, int>
        {
            [stats.Key("critical_strike_chance_+%")!.Value] = 20,
            [stats.Key("spell_critical_strike_chance_+%")!.Value] = 30,
            [stats.Key("spell_critical_strike_chance")!.Value] = 9999,
        };
        int pulse = skills.EffectRow("FreezingPulse");
        var own = skills.EffectStats(pulse, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        own[stats.Key("critical_strike_chance_+%")!.Value] = 50;
        Assert.Equal(1500, SpellCriticalStrike.Chance(skills.SpellCritChance(pulse, 1), stats, player, own));
        Assert.Equal(9999, player[stats.Key("spell_critical_strike_chance")!.Value]);
        int ray = skills.EffectRow("ScorchingRay");
        Assert.Equal(0, SpellCriticalStrike.Chance(skills.SpellCritChance(ray, 1), stats, player,
            skills.EffectStats(ray, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2)));
    }

    [Theory]
    [InlineData("ScorchingRay", true)]
    [InlineData("RighteousFire", true)]
    [InlineData("EnduringCry", false)]
    [InlineData("NoSuchEffect", false)]
    public void Hitless_spells_and_unclassified_actions_never_borrow_weapon_damage_or_attack_crit(
        string effect, bool spell)
    {
        var skills = new SkillCatalog(Data());
        int row = skills.EffectRow(effect);
        var plan = SkillHitPlan.ForSkill(skills, row, 1);
        Assert.Equal(spell ? SkillHitKind.Spell : SkillHitKind.Unsupported, plan.Kind);
        Assert.True(plan.SpellBase.IsZero);
        Assert.False(plan.CanHit);
        Assert.Same(DamagePacket.Empty, plan.BuildHit(
            () => throw new Exception("A hitless action must not construct a weapon hit"),
            _ => throw new Exception("Added spell damage must not fabricate a hit for a hitless spell")));
        Assert.Equal(0, plan.CriticalChance(10000,
            () => throw new Exception("There is no hit to roll critical chance for")));
        if (effect == "ScorchingRay") Assert.Equal(0, skills.SpellCritChance(row, 1));
    }

    [Fact]
    public void Real_spell_and_attack_plans_keep_their_separate_damage_and_crit_routes()
    {
        var skills = new SkillCatalog(Data());
        var arc = SkillHitPlan.ForSkill(skills, skills.EffectRow("Arc"), 1);
        Assert.True(arc.CanHit);
        Assert.Equal(SkillHitKind.Spell, arc.Kind);
        Assert.Same(arc.SpellBase, arc.BuildHit(() => throw new Exception("Arc is not a weapon hit"), hit => hit));
        Assert.Equal(600, arc.CriticalChance(10000,
            () => SpellCriticalStrike.Chance(skills.SpellCritChance(skills.EffectRow("Arc"), 1), Values())));

        var attack = SkillHitPlan.ForSkill(skills, skills.EffectRow("PlayerMelee"), 1);
        var weapon = DamagePacket.Of(DamageType.Physical, 10, 20);
        Assert.True(attack.CanHit);
        Assert.Same(weapon, attack.BuildHit(() => weapon, _ => throw new Exception("An attack keeps its weapon")));
        Assert.Equal(830, attack.CriticalChance(830, () => throw new Exception("An attack keeps its critical chance")));
    }

    [Theory]
    [InlineData("BurningArrow")]
    [InlineData("CausticArrow")]
    public void Bow_projectile_gems_keep_weapon_damage_and_attack_crit_despite_zero_spell_base(string effect)
    {
        var skills = new SkillCatalog(Data());
        int row = skills.EffectRow(effect);
        Assert.NotNull(skills.ProjectileFor(row));
        Assert.Equal(0, skills.SpellCritChance(row, 1));
        var plan = SkillHitPlan.ForSkill(skills, row, 1);
        var weapon = DamagePacket.Of(DamageType.Physical, 10, 20);
        Assert.Equal(SkillHitKind.Attack, plan.Kind);
        Assert.True(plan.CanHit);
        Assert.Same(weapon, plan.BuildHit(() => weapon, _ => throw new Exception("An arrow is not spell damage")));
        Assert.Equal(830, plan.CriticalChance(830, () => throw new Exception("An arrow uses attack crit")));
    }

    [Fact]
    public void Type_precedence_and_fractional_damage_do_not_depend_on_a_trial_roll()
    {
        var fractional = DamagePacket.Of(DamageType.Fire, .1, .9);
        Assert.True(SkillHitPlan.Create(attack: false, spell: true, fractional).CanHit);
        var mixed = SkillHitPlan.Create(attack: true, spell: true, fractional);
        Assert.Equal(SkillHitKind.Attack, mixed.Kind);
        Assert.True(mixed.CanHit);
        var unknown = SkillHitPlan.Create(attack: false, spell: false, fractional);
        Assert.False(unknown.CanHit);
        Assert.Same(DamagePacket.Empty, unknown.BuildHit(() => fractional, _ => fractional));
        Assert.Equal(0, unknown.CriticalChance(10000, () => 10000));
    }

    [Theory]
    [InlineData(750, 3, 773)]
    [InlineData(750, -3, 728)]
    [InlineData(500, 100, 1000)]
    [InlineData(750, 2000, 10000)]
    [InlineData(750, -100, 0)]
    [InlineData(750, -200, 0)]
    [InlineData(0, 2000, 0)]
    public void Chance_rounds_to_integer_permyriad_and_respects_the_range(int baseChance, int increased, int expected) =>
        Assert.Equal(expected, SpellCriticalStrike.Chance(baseChance, Values(("spell_critical_strike_chance_+%", increased))));

    [Fact]
    public void Extreme_raw_values_cannot_overflow_the_chance()
    {
        Assert.Equal(10000, SpellCriticalStrike.Chance(int.MaxValue,
            Values(("critical_strike_chance_+%", int.MaxValue), ("spell_critical_strike_chance_+%", int.MaxValue)),
            Values(("critical_strike_chance_+%", int.MaxValue), ("spell_critical_strike_chance_+%", int.MaxValue))));
        Assert.Equal(0, SpellCriticalStrike.Chance(750, Values(("critical_strike_chance_+%", int.MinValue))));
    }

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    public void Supported_disabled_flags_apply_to_player_and_skill(string name)
    {
        Assert.Equal(0, SpellCriticalStrike.Chance(750, Values((name, 1))));
        Assert.Equal(0, SpellCriticalStrike.Chance(750, Values(), Values((name, 1))));
        Assert.Equal(750, SpellCriticalStrike.Chance(750, Values(("cannot_critical_strike_with_attacks", 1))));
    }

    [Fact]
    public void Missing_or_invalid_base_fails_closed_without_defaulting_to_Fireball()
    {
        Assert.Equal(0, SpellCriticalStrike.Chance(null, Values(("critical_strike_chance_+%", 10000))));
        Assert.Equal(0, SpellCriticalStrike.Chance(-1, Values()));
        var skills = new SkillCatalog(Data());
        Assert.Null(skills.SpellCritChance(-1, 1));
        Assert.Null(skills.SpellCritChance(int.MaxValue, 1));
        Assert.Null(skills.SpellCritChance(skills.EffectRow("Fireball"), 0));
        Assert.Null(new SkillCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).SpellCritChance(0, 1));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(750, 749, true)]
    [InlineData(750, 750, false)]
    [InlineData(10000, 9999, true)]
    [InlineData(10000, -1, false)]
    [InlineData(10000, 10000, false)]
    public void Rolls_use_exact_permyriad_boundaries(int chance, int roll, bool expected) =>
        Assert.Equal(expected, SpellCriticalStrike.Hits(chance, roll));

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void Precision_and_Zealotry_raw_sources_match_the_existing_pool_bonus_once(int auraEffect)
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[]
        {
            new AuraCatalog.Aura(0x103, skills.EffectRow("Precision"), "Precision", "Precision", 1, 1293, 22, 0),
            new AuraCatalog.Aura(0x104, skills.EffectRow("Zealotry"), "Zealotry", "Zealotry", 2, 0, 0, 5000),
        };
        var bonus = AuraCatalog.Bonus(auras, auraEffect, skills, stats);
        var raw = AuraCatalog.RawStats(auras, auraEffect, skills, stats).ToDictionary(kv => kv.Key, kv => kv.Value);
        int Get(string name) => stats.Key(name) is uint k ? raw.GetValueOrDefault(k) : 0;
        Assert.True(bonus.CriticalStrikeChancePct > 0);
        Assert.True(bonus.SpellCriticalStrikeChancePct > bonus.CriticalStrikeChancePct);
        Assert.Equal(bonus.CriticalStrikeChancePct, Get("critical_strike_chance_+%"));
        Assert.Equal(bonus.SpellCriticalStrikeChancePct,
            Get("critical_strike_chance_+%") + Get("spell_critical_strike_chance_+%"));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 20,
            new CharacterStats.Bonus(SpellCriticalStrikeChancePct: bonus.SpellCriticalStrikeChancePct));
        Assert.Equal(pools.SpellCriticalStrikeChance, SpellCriticalStrike.Chance(500, Get));
        Assert.Equal((int)Math.Round(750 * (100m + bonus.SpellCriticalStrikeChancePct) / 100m,
            MidpointRounding.AwayFromZero), SpellCriticalStrike.Chance(750, Get));
    }
}

using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ArcaneMightTests
{
    static readonly StatCatalog Catalog = new();
    static readonly SkillCatalog Skills = new();
    const string Ratio = "additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value";
    const string SkillRatio = "active_skill_additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value";
    const string Basic = "additive_spell_damage_modifiers_apply_to_attack_damage";

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 25)]
    [InlineData(false, true, 50)]
    [InlineData(true, true, 50)]
    public void Conditional_grants_require_context_in_both_query_forms(bool wand, bool retaliation, double expected)
    {
        var stats = new Dictionary<uint, int>
        {
            [Catalog.Key(ArcaneMight.Wand)!.Value] = 100,
            [Catalog.Key(ArcaneMight.Retaliation)!.Value] = 200,
            [StatKeys.SpellDamagePct] = 25,
        };
        int Get(string name) => Catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Assert.Equal(expected, DamageStats.SpellAttackIncrease(stats, wand, retaliation));
        Assert.Equal(expected, DamageStats.SpellAttackIncrease(Get, wand, retaliation));
        Assert.Equal(0, DamageStats.SpellAttackIncrease(new Dictionary<uint, int> { [StatKeys.SpellDamagePct] = 1000 }));
    }

    [Theory]
    [InlineData("KineticBolt", 0, 200)]
    [InlineData("KineticBolt", 1, 202)]
    [InlineData("KineticBolt", 20, 250)]
    [InlineData("KineticBoltAltY", 20, 250)]
    [InlineData("HeavyStrikeAltY", 20, 170)]
    public void Real_skill_quality_improves_its_candidate_before_comparing_independent_grants(string effect, int quality, int expected)
    {
        var skills = Skills;
        var own = skills.EffectStats(skills.EffectRow(effect), 1, quality).ToDictionary(v => v.Item1, v => v.Item2);
        uint key = Catalog.Key(SkillRatio)!.Value;
        Assert.Equal(expected, own[key]);
        var actor = new Dictionary<uint, int> { [key] = 150, [StatKeys.SpellDamagePct] = 20 };
        var merge = typeof(Program).GetMethod("MergeSkillStats",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var merged = (Dictionary<uint, int>)merge.Invoke(null, [actor, own])!;
        Assert.Equal(expected, merged[key]);
        Assert.Equal(expected / 5.0, DamageStats.SpellAttackIncrease(merged));
        Assert.Equal(30, DamageStats.SpellAttackIncrease(actor));
    }

    [Theory]
    [InlineData("HeavyStrikeAltMercenary", 150)]
    [InlineData("HeavyStrikeAltY", 150)]
    [InlineData("PowerSiphonMercenary", 150)]
    [InlineData("PowerSiphon", 150)]
    [InlineData("PowerSiphonAltX", 150)]
    [InlineData("PowerSiphonAltY", 150)]
    [InlineData("VaalPowerSiphon", 150)]
    [InlineData("GigaKineticBlastMercenary", 200)]
    [InlineData("KineticBlastAltMercenary", 200)]
    [InlineData("KineticBlast", 150)]
    [InlineData("KineticBlastAltX", 150)]
    [InlineData("KineticBlastAltY", 150)]
    [InlineData("KineticBoltMercenary", 200)]
    [InlineData("KineticBolt", 200)]
    [InlineData("KineticBoltAltY", 200)]
    [InlineData("KineticRain", 200)]
    [InlineData("KineticRainKineticInstability", 200)]
    [InlineData("KineticRainAltXMercenary", 200)]
    [InlineData("KineticRainAltX", 200)]
    [InlineData("SomaticShell", 150)]
    [InlineData("KineticFusillade", 150)]
    [InlineData("KineticFusilladeAltX", 150)]
    [InlineData("TriggeredSupportGreaterKineticInstability", 150)]
    [InlineData("TriggeredSupportKineticInstability", 150)]
    public void Inventoried_skill_constants_load_without_grant_leakage(string effect, int ratio)
    {
        var own = Skills.EffectStats(Skills.EffectRow(effect), 1, 0).ToDictionary(v => v.Item1, v => v.Item2);
        Assert.Equal(ratio, own[StatKeys.SkillSpellDamageIncreasesApplyToAttacks]);
        own[StatKeys.SpellDamagePct] = 20;
        Assert.Equal(ratio / 5.0, DamageStats.SpellAttackIncrease(own));
        Assert.Equal(0, DamageStats.SpellAttackIncrease(new Dictionary<uint, int> { [StatKeys.SpellDamagePct] = 20 }));
    }

    [Fact]
    public void Real_item_mods_and_mastery_keep_their_own_grant_contracts()
    {
        var items = new ItemCatalog();
        Item With(string id) => ItemCatalog.Create(items.Find("Iron Ring")!) with
        {
            Mods = [new(GameServer.Core.Protocol.Instance.ModSection.Explicit, items.Mods.ById(id)!.Hash,
                [items.Mods.ById(id)!.Stats[0].Min])],
        };
        var foulborn = With("DivergentSpellDamageModifiersApplyToAttackDamageUniqueHelmetInt7");
        var retaliation = With("MutatedUniqueHelmetInt10AdditiveSpellModifiersApplyToRetaliationAttackDamage");
        var stats = ItemModStats.Global([foulborn, retaliation, retaliation], items.Mods, Catalog);
        stats[StatKeys.SpellDamagePct] = 20;
        Assert.Equal(20, DamageStats.SpellAttackIncrease(stats));
        Assert.Equal(40, DamageStats.SpellAttackIncrease(stats, retaliation: true));
        var tree = new PassiveTree();
        var mastery = Assert.Single(tree.EffectsFor(53828), e => e.Hash == 61352);
        Assert.Equal("Wand4", mastery.Id);
        var grants = tree.Stats([53828], new Dictionary<ushort, ushort> { [53828] = mastery.Hash });
        grants[StatKeys.SpellDamagePct] = 20;
        Assert.Equal(20, DamageStats.SpellAttackIncrease(grants, wieldingWand: true));
        Assert.Equal(0, DamageStats.SpellAttackIncrease(grants));
    }

    [Theory]
    [InlineData(false, 130, 130)]
    [InlineData(true, 170, 150)]
    public void Ordinary_weapon_components_use_actual_melee_context_with_the_transfer(bool melee, double physical, double fire)
    {
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 10)
        {
            Melee = melee, LocalAdded = DamagePacket.Of(DamageType.Fire, 100, 100),
        };
        var stats = new Dictionary<uint, int>
        {
            [StatKeys.SpellDamagePct] = 20, [StatKeys.SpellDamageIncreasesApplyToAttacks150] = 1,
            [StatKeys.MeleeDamagePct] = 20, [StatKeys.MeleePhysicalDamagePct] = 20,
        };
        var packet = Program.AttackHit(weapon, stats, melee);
        Assert.Equal(physical, packet[DamageType.Physical].Min);
        Assert.Equal(fire, packet[DamageType.Fire].Min);
    }

    [Theory]
    [InlineData(Basic, 1, 20, 20)]
    [InlineData(Ratio, 175, 20, 35)]
    [InlineData(SkillRatio, 200, 20, 40)]
    [InlineData(Ratio, 150, -20, -30)]
    [InlineData(Ratio, 150, 1, 1.5)]
    public void Both_query_forms_apply_the_same_signed_spell_increase(string source, int value, int spell, double expected)
    {
        var stats = new Dictionary<uint, int>
        {
            [Catalog.Key(source)!.Value] = value,
            [StatKeys.SpellDamagePct] = spell,
        };
        int Get(string name) => Catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Assert.Equal(expected, DamageStats.SpellAttackIncrease(Get));
        Assert.Equal(expected, DamageStats.SpellAttackIncrease(stats));
    }

    [Theory]
    [InlineData(20, 40)]
    [InlineData(-20, -40)]
    public void Multiple_sources_choose_the_highest_ratio_even_for_reductions(int spell, double expected)
    {
        int Get(string name) => name switch
        {
            Basic => 1,
            DamageStats.SpellDamageIncreasesApplyToAttacks150 => 2,
            Ratio => 200,
            SkillRatio => 150,
            "spell_damage_+%" => spell,
            _ => 0,
        };
        Assert.Equal(expected, DamageStats.SpellAttackIncrease(Get));
    }

    [Fact]
    public void Actor_and_skill_ratios_are_alternatives_not_additive_percentages()
    {
        uint key = Catalog.Key(SkillRatio)!.Value;
        var actor = new Dictionary<uint, int> { [key] = 200, [StatKeys.SpellDamagePct] = 20 };
        var own = new Dictionary<uint, int> { [key] = 150 };
        var merge = typeof(Program).GetMethod("MergeSkillStats",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var merged = (Dictionary<uint, int>)merge.Invoke(null, [actor, own])!;
        int Get(string name) => Catalog.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;
        Assert.Equal(40, DamageStats.SpellAttackIncrease(Get));
        Assert.Equal(200, actor[key]);
        Assert.Equal(150, own[key]);
    }

    [Fact]
    public void Independent_actor_percentage_sources_keep_the_highest_grant()
    {
        uint key = Catalog.Key(Ratio)!.Value;
        var stats = ItemModStats.Plus(new() { [key] = 175 }, new() { [key] = 200 });
        stats[StatKeys.SpellDamagePct] = 20;
        Assert.Equal(40, DamageStats.SpellAttackIncrease(stats));
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 140)]
    public void Retaliation_only_grant_is_not_a_global_attack_bonus(bool retaliation, double expected)
    {
        int Get(string name) => name switch
        {
            "additive_spell_damage_modifiers_apply_to_retaliation_attack_damage_at_%_value" => 200,
            "spell_damage_+%" => 20,
            _ => 0,
        };
        var types = new HashSet<int> { 0 };
        if (retaliation) types.Add(120);
        Assert.Equal(expected, SupportedAttackDamage.Hit(new(1000, 100, 100, 10), Get, types)[DamageType.Physical].Min);
    }

    [Fact]
    public void Weapon_local_and_global_added_components_each_receive_the_transfer_once()
    {
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 10)
            { LocalAdded = DamagePacket.Of(DamageType.Fire, 100, 100) };
        var stats = new Dictionary<uint, int>
        {
            [StatKeys.SpellDamageIncreasesApplyToAttacks150] = 1,
            [StatKeys.SpellDamagePct] = 20,
            [Catalog.Key("attack_minimum_added_cold_damage")!.Value] = 100,
            [Catalog.Key("attack_maximum_added_cold_damage")!.Value] = 100,
            [Catalog.Key("active_skill_spell_damage_+%_final")!.Value] = 1000,
            [Catalog.Key("spell_minimum_added_lightning_damage")!.Value] = 1000,
            [Catalog.Key("base_cast_speed_+%")!.Value] = 1000,
        };
        int Get(string name) => Catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        foreach (var hit in new[] { Program.AttackHit(weapon, stats, false),
            SupportedAttackDamage.Hit(weapon, Get, new HashSet<int> { 0, 2 }) })
        {
            Assert.Equal(130, hit[DamageType.Physical].Min);
            Assert.Equal(130, hit[DamageType.Fire].Min);
            Assert.Equal(130, hit[DamageType.Cold].Min);
            Assert.Equal(0, hit[DamageType.Lightning].Max);
        }
        stats[Catalog.Key("maximum_mana")!.Value] = 1000;
        stats[Catalog.Key("added_physical_damage_to_attacks_equal_to_%_maximum_mana")!.Value] = 10;
        stats[Catalog.Key("attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana")!.Value] = 10;
        foreach (var hit in new[] { Program.AttackHit(weapon, stats, false),
            SupportedAttackDamage.Hit(weapon, Get, new HashSet<int> { 0, 2 }) })
        {
            Assert.Equal(260, hit[DamageType.Physical].Min); // (100 weapon + 100 mana) * 1.3.
            Assert.Equal(130, hit[DamageType.Lightning].Min); // 100 mana * 1.3, not multiplied twice.
            Assert.Equal(130, hit[DamageType.Fire].Min);
            Assert.Equal(130, hit[DamageType.Cold].Min);
        }
    }
}

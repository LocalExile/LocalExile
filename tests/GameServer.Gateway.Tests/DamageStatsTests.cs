using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DamageStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Skill_spell_increase_transfer_keeps_half_percent_and_does_not_stack_with_global_flag()
    {
        int Get(string name) => name switch
        {
            "active_skill_additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value" => 150,
            "spell_damage_+%" => 1,
            DamageStats.SpellDamageIncreasesApplyToAttacks150 => 1,
            _ => 0
        };
        var hit = SupportedAttackDamage.Hit(new(1000, 200, 200, 10), Get, new HashSet<int> { 0, 2 });
        Assert.Equal(203, hit[DamageType.Physical].Min);
        int OnlySkill(string name) => name == DamageStats.SpellDamageIncreasesApplyToAttacks150 ? 0 : Get(name);
        Assert.Equal(203, SupportedAttackDamage.Hit(new(1000, 200, 200, 10), OnlySkill,
            new HashSet<int> { 0, 2 })[DamageType.Physical].Min);
    }

    [Fact]
    public void Maximum_mana_physical_added_damage_is_attack_only_and_precedes_conversion()
    {
        int Get(string name) => name switch
        {
            "maximum_mana" => 997,
            "added_physical_damage_to_attacks_equal_to_%_maximum_mana" => 10,
            "physical_damage_+%" => 100,
            _ => 0
        };
        var conversion = new DamageConversion(); conversion.AddGlobal(DamageType.Physical, DamageType.Fire, 50);
        var attack = SupportedAttackDamage.Hit(new(1000, 0, 0, 10), Get, new HashSet<int> { 0, 2 }).Convert(conversion);
        Assert.Equal(99.7, attack[DamageType.Physical].Min, 6);
        Assert.Equal(99.7, attack[DamageType.Fire].Min, 6);
        var added = DamageStats.Added(DamagePacket.Empty, Get, spell: false, melee: false, effectiveness: .5);
        Assert.Equal(99.7, added[DamageType.Physical].Min, 6);
        Assert.True(DamageStats.Added(DamagePacket.Empty, Get, spell: true, melee: false).IsZero);
    }

    [Fact]
    public void Conversion_reads_the_global_and_skill_stats()
    {
        var catalog = new StatCatalog(Data());
        var stats = new Dictionary<uint, int>
        {
            [catalog.Key("physical_damage_%_to_convert_to_fire")!.Value] = 50,
            [catalog.Key("skill_physical_damage_%_to_convert_to_cold")!.Value] = 25,
        };

        var result = DamagePacket.Of(DamageType.Physical, 100, 100)
            .Convert(DamageStats.Conversion(stats, catalog, attack: true));

        Assert.Equal(25, result[DamageType.Physical].Min);
        Assert.Equal(50, result[DamageType.Fire].Min);
        Assert.Equal(25, result[DamageType.Cold].Min);
        Assert.Equal(100, result.MinTotal);
    }

    [Fact]
    public void Attack_and_spell_conversion_stats_only_apply_to_their_hit()
    {
        var catalog = new StatCatalog(Data());
        var stats = new Dictionary<uint, int>
        {
            [catalog.Key("attack_physical_damage_%_to_convert_to_fire")!.Value] = 50,
        };

        var attack = DamagePacket.Of(DamageType.Physical, 100, 100)
            .Convert(DamageStats.Conversion(stats, catalog, attack: true));
        var spell = DamagePacket.Of(DamageType.Physical, 100, 100)
            .Convert(DamageStats.Conversion(stats, catalog, attack: false));

        Assert.Equal(50, attack[DamageType.Fire].Min);
        Assert.Equal(0, spell[DamageType.Fire].Min);
        Assert.Equal(100, spell[DamageType.Physical].Min);
    }

    [Fact]
    public void Gain_as_extra_keeps_the_source_damage()
    {
        var catalog = new StatCatalog(Data());
        var stats = new Dictionary<uint, int>
        {
            [catalog.Key("physical_damage_%_to_add_as_lightning")!.Value] = 25,
        };

        var result = DamagePacket.Of(DamageType.Physical, 80, 120)
            .Convert(DamageStats.Conversion(stats, catalog, attack: true));

        Assert.Equal(80, result[DamageType.Physical].Min);
        Assert.Equal(120, result[DamageType.Physical].Max);
        Assert.Equal(20, result[DamageType.Lightning].Min);
        Assert.Equal(30, result[DamageType.Lightning].Max);
    }

    [Fact]
    public void A_spell_skill_keeps_its_base_damage_by_type()
    {
        var skills = new SkillCatalog(Data());
        int fireball = skills.EffectRow("Fireball");
        Assert.True(fireball >= 0);

        var hit = skills.HitDamage(fireball, 1);

        Assert.Equal(9, hit[DamageType.Fire].Min);
        Assert.Equal(14, hit[DamageType.Fire].Max);
    }
}

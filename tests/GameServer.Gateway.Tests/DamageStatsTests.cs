using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DamageStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
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

}

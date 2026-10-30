using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AttackPanelStatsTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new(BenchCraftingTests.Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new(BenchCraftingTests.Data()));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static SkillCatalog.Unarmed Weapon(int min = 10, int max = 20) => new(714, min, max, 8);
    static DamagePacket Resolve(string effect, IReadOnlyDictionary<uint, int> character, SkillCatalog.Unarmed? weapon = null)
    {
        int row = Skills.Value.EffectRow(effect);
        return AttackSkillHit.Resolve(weapon ?? Weapon(), character, Skills.Value.EffectStats(row, 1),
            Stats.Value, Skills.Value.DamageEffectiveness(row, 1), melee: Skills.Value.IsMelee(row),
            projectile: Program.Supports.Of(row)?.Types.Contains(2) == true);
    }
    static Dictionary<string, int> Rows(string effect, DamagePacket hit, int aps = 140) =>
        AttackPanelStats.Compute(SkillHitPlan.ForSkill(Skills.Value, Skills.Value.EffectRow(effect), 1), hit, aps / 100.0)
            .ToDictionary(r => r.Stat, r => r.Value);

    [Fact]
    public void Real_Burning_Arrow_uses_280_percent_effectiveness_and_full_skill_fire_conversion()
    {
        int effect = Skills.Value.EffectRow("BurningArrow");
        Assert.Equal(28000, Skills.Value.DamageEffectiveness(effect, 1));
        Assert.Contains(Skills.Value.EffectStats(effect, 1), s =>
            s.Item1 == Stats.Value.Key("skill_physical_damage_%_to_convert_to_fire") && s.Item2 == 100);
        var hit = Resolve("BurningArrow", Values());
        Assert.Equal(new DamageRange(28, 56), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(0, 0), hit[DamageType.Physical]);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(28, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(56, rows["main_hand_maximum_fire_damage"]);
        Assert.Equal(0, rows["main_hand_maximum_physical_damage"]);
        Assert.Equal(4200, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(5880, rows["hundred_times_damage_per_second"]);
        // A separately corrected APS value is consumed directly, without recomputing skill speed here.
        Assert.Equal(4116, Rows("BurningArrow", hit, 98)["hundred_times_damage_per_second"]);
    }

}

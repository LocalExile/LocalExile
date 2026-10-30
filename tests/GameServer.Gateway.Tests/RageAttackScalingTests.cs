using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class RageAttackScalingTests
{
    const string RageDamage = "attack_damage_+%_final_from_rage";
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Program.Stats.Key(v.Name)!.Value, v => v.Value);

    public static IEnumerable<object[]> Percentages() => Enumerable.Range(0, 46).Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(Percentages))]
    public void Default_skill_and_bow_packets_keep_fixed_hundred_bounds_and_fractional_mana_damage(int percent)
    {
        var stats = Values((RageDamage, percent), ("attack_minimum_added_chaos_damage", 100),
            ("attack_maximum_added_chaos_damage", 100), ("attack_minimum_added_lightning_damage", 1),
            ("attack_maximum_added_lightning_damage", 2), ("maximum_mana", 1350), (DamageStats.MaxManaAddedLightning, 5));
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var packets = new[]
        {
            Program.AttackHit(weapon, stats, melee: true),
            AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, 10000, melee: true),
            OrdinaryRangedShot.Capture(weapon, stats, [], Program.Stats, 1000, 0, 150).Hit
        };
        foreach (var hit in packets)
        {
            foreach (var type in new[] { DamageType.Physical, DamageType.Chaos })
            {
                Assert.Equal(new DamageRange(100 + percent, 100 + percent), hit[type]);
                Assert.Equal(100 + percent, hit[type].Roll(new Random(1)));
            }
            Assert.Equal((double)(68.5m * (100 + percent) / 100), hit[DamageType.Lightning].Min, 12);
            Assert.Equal((double)(69.5m * (100 + percent) / 100), hit[DamageType.Lightning].Max, 12);
            var panel = PanelStats.HandDamage(hit, "main_hand").ToDictionary(s => s.Stat, s => s.Value);
            Assert.Equal(100 + percent, panel["main_hand_minimum_chaos_damage"]);
            Assert.Equal(100 + percent, panel["main_hand_maximum_physical_damage"]);
        }
    }

}

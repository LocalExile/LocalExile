using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class OrdinaryRangedShotTests
{
    [Fact]
    public void Capture_keeps_existing_weapon_added_conversion_precedence_and_freezes_the_inputs()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "tables"));
        try
        {
            string[] names = ["attack_minimum_added_fire_damage", "attack_maximum_added_fire_damage",
                "physical_damage_%_to_convert_to_fire", "impale_on_hit_%_chance", "base_reduce_enemy_fire_resistance_%",
                "critical_strikes_ignore_elemental_resistances", "non_critical_strikes_penetrate_elemental_resistances_%"];
            File.WriteAllText(Path.Combine(folder, "tables", "Stats.json"),
                System.Text.Json.JsonSerializer.Serialize(names.Select(name => new { Id = name })));
            var catalog = new StatCatalog(folder);
            var raw = new Dictionary<uint, int> { [1] = 3, [2] = 7, [3] = 50, [4] = 25, [5] = 20, [6] = 1, [7] = 7,
                [StatKeys.MeleePhysicalDamagePct] = 20 };
            var weapon = new SkillCatalog.Unarmed(714, 5, 13, 120);
            var shot = OrdinaryRangedShot.Capture(weapon, raw, [(3, 100), (5, 10), (7, 3)], catalog, 321, 1234, 175);
            // Retain the existing ordinary attack's melee-physical treatment, and the player's conversion wins
            // a colliding default-skill key. Added fire is added before conversion, exactly once.
            Assert.Equal(new DamageRange(3, 7.8), shot.Hit[DamageType.Physical]);
            Assert.Equal(new DamageRange(6, 14.8), shot.Hit[DamageType.Fire]);
            raw.Clear();
            weapon = weapon with { MinDamage = 1000, MaxDamage = 2000 };
            Assert.Equal(new DamageRange(3, 7.8), shot.Hit[DamageType.Physical]);
            Assert.Equal((321, 1234, 175, 25), (shot.Accuracy, shot.CriticalChance, shot.CriticalMultiplier, shot.ImpaleChance));
            Assert.Equal(30, shot.Penetration.Fire);
            Assert.Equal(1, shot.ResistanceMods.CriticalIgnoreElemental);
            Assert.Equal(10, shot.ResistanceMods.NonCriticalPenetration);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    sealed class Rolls(params int[] values) : Random
    {
        int index;
        public override int Next(int maxValue) => values[index++] % maxValue;
        public override int Next(int minValue, int maxValue) => minValue + values[index++] % (maxValue - minValue);
    }

}

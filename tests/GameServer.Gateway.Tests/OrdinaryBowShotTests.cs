using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class OrdinaryBowShotTests
{
    [Fact]
    public void Collision_accuracy_uses_frozen_masteries_but_live_target_rarity_and_both_actors_movement()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 0, 0, null, []), 1, 1);
        var normal = new SpawnedMonster("moving normal", new WalkEstimator(10, 0, 10), 0, 0, 100);
        var unique = new SpawnedMonster("moving unique", new WalkEstimator(10, 0, 10), 0, 0, 100)
        { Rarity = MonsterRarity.Unique };
        var shot = new OrdinaryBowShot(DamagePacket.Empty, 1000, 0, 150, 0)
        { AccuracyMoreVsUniquePct = 40, AccuracyMoreInCloseRangePct = 50 };
        normal.Walk.Order(40, 0, 0);
        unique.Walk.Order(40, 0, 0);
        Assert.Equal(1500, Program.OrdinaryBowAccuracyAt(conn, normal, shot, 0));
        Assert.Equal(2100, Program.OrdinaryBowAccuracyAt(conn, unique, shot, 0));
        Assert.Equal(1000, Program.OrdinaryBowAccuracyAt(conn, normal, shot, 3));
        Assert.Equal(1400, Program.OrdinaryBowAccuracyAt(conn, unique, shot, 3));
        conn.Walk.Teleport(35, 0, 3);
        conn.Pools = conn.Pools with { Accuracy = 99999, AccuracyMoreVsUniquePct = 0, AccuracyMoreInCloseRangePct = 0 };
        Assert.Equal(1500, Program.OrdinaryBowAccuracyAt(conn, normal, shot, 3));
        Assert.Equal(2100, Program.OrdinaryBowAccuracyAt(conn, unique, shot, 3));
    }

    [Fact]
    public void Capture_keeps_existing_weapon_added_conversion_precedence_and_freezes_the_inputs()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "tables"));
        try
        {
            string[] names = ["attack_minimum_added_fire_damage", "attack_maximum_added_fire_damage",
                "physical_damage_%_to_convert_to_fire", "impale_on_hit_%_chance"];
            File.WriteAllText(Path.Combine(folder, "tables", "Stats.json"),
                System.Text.Json.JsonSerializer.Serialize(names.Select(name => new { Id = name })));
            var catalog = new StatCatalog(folder);
            var raw = new Dictionary<uint, int> { [1] = 3, [2] = 7, [3] = 50, [4] = 25,
                [StatKeys.MeleePhysicalDamagePct] = 20 };
            var weapon = new SkillCatalog.Unarmed(714, 5, 13, 120);
            var shot = OrdinaryBowShot.Capture(weapon, raw, [(3, 100)], catalog, 321, 1234, 175);
            // A bow excludes melee-only damage increases, and the player's conversion wins
            // a colliding default-skill key. Added fire is added before conversion, exactly once.
            Assert.Equal(new DamageRange(2.5, 6.5), shot.Hit[DamageType.Physical]);
            Assert.Equal(new DamageRange(5.5, 13.5), shot.Hit[DamageType.Fire]);
            raw.Clear();
            weapon = weapon with { MinDamage = 1000, MaxDamage = 2000 };
            Assert.Equal(new DamageRange(2.5, 6.5), shot.Hit[DamageType.Physical]);
            Assert.Equal((321, 1234, 175, 25), (shot.Accuracy, shot.CriticalChance, shot.CriticalMultiplier, shot.ImpaleChance));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    sealed class Rolls(params int[] values) : Random
    {
        int index;
        public override int Next(int maxValue) => values[index++] % maxValue;
        public override int Next(int minValue, int maxValue) => minValue + values[index++] % (maxValue - minValue);
    }

    [Fact]
    public void Accuracy_uses_the_contact_targets_evasion_without_mutating_its_impales()
    {
        var shot = new OrdinaryBowShot(DamagePacket.Of(DamageType.Physical, 10, 20), 100, 0, 150, 100);
        var impale = new ImpaleState();
        impale.Apply(100);
        Assert.True(shot.Hits(0, new Rolls(99)));
        Assert.False(shot.Hits(100_000, new Rolls(99)));
        Assert.Equal(1, impale.Stacks);
        Assert.Equal(10, impale.Bonus);
    }

    [Fact]
    public void Existing_impales_join_after_crit_and_a_new_stack_keeps_five_hits()
    {
        var shot = new OrdinaryBowShot(DamagePacket.Of(DamageType.Physical, 10, 20), 100, 10_000, 200, 100);
        var impale = new ImpaleState();
        impale.Apply(100);
        var result = shot.Roll(impale, new Rolls(0, 0, 0));
        Assert.Equal((30, true), result); // 10 minimum * 200% + 10 existing impale.
        Assert.Equal(12, impale.Bonus); // Existing 10 plus 10% of the unmodified physical maximum 20.
        for (int i = 0; i < 4; i++) impale.AfterHit();
        Assert.Equal(2, impale.Bonus); // The older stack expires first; the newly applied one retains its final hit.
    }

    [Fact]
    public void Converted_damage_does_not_invent_physical_impales_and_added_types_roll_separately()
    {
        var hit = DamagePacket.Of(DamageType.Fire, 18, 36).Add(DamageType.Cold, new(5, 5));
        var shot = new OrdinaryBowShot(hit, 100, 0, 150, 100);
        var impale = new ImpaleState();
        Assert.Equal((23, false), shot.Roll(impale, new Rolls(0, 0, 0)));
        Assert.Equal(0, impale.Stacks);
        Assert.Equal(new DamageRange(18, 36), shot.Hit[DamageType.Fire]);
    }

    [Fact]
    public void Each_actual_target_uses_its_own_stacks_and_a_shot_keeps_its_release_inputs()
    {
        var shot = new OrdinaryBowShot(DamagePacket.Of(DamageType.Physical, 10, 10), 400, 0, 150, 0);
        var first = new ImpaleState(); first.Apply(100);
        var second = new ImpaleState(); second.Apply(200);
        Assert.Equal((20, false), shot.Roll(first, new Rolls(0)));
        Assert.Equal((30, false), shot.Roll(second, new Rolls(0)));
        Assert.Equal(400, shot.Accuracy);
        Assert.Equal(10, shot.Hit.MinTotal);
    }
}

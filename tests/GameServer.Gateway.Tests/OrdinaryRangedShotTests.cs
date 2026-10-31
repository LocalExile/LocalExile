using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class OrdinaryRangedShotTests
{
    [Fact]
    public void Capture_freezes_general_and_weapon_impale_settings_until_the_next_shot()
    {
        var local = new Dictionary<string, int> { ["local_attacks_impale_on_hit_%_chance"] = 15 };
        var actor = new Dictionary<uint, int>
        {
            [Program.Stats.Key("attacks_impale_on_hit_%_chance")!.Value] = 40,
            [Program.Stats.Key("impale_debuff_effect_+%")!.Value] = 50,
            [Program.Stats.Key("impaled_debuff_number_of_reflected_hits")!.Value] = 2,
            [Program.Stats.Key("impaled_debuff_duration_+%")!.Value] = 25,
        };
        var weapon = new SkillCatalog.Unarmed(1000, 10, 10, 120) { LocalSources = local };
        var shot = OrdinaryRangedShot.Capture(weapon, actor, [], Program.Stats, 100, 0, 150);
        actor.Clear(); local.Clear();
        var next = OrdinaryRangedShot.Capture(weapon, actor, [], Program.Stats, 100, 0, 150);
        Assert.Equal(new ImpaleStats.Settings(55, 50, 7, 10), shot.ImpaleSettings);
        Assert.Equal(new ImpaleStats.Settings(0), next.ImpaleSettings);
    }

    [Fact]
    public void Stored_critical_physical_damage_precedes_target_mitigation_and_critical_damage_taken()
    {
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 100, 100), 100, 10000, 200, 100);
        var impale = new ImpaleState();
        impale.Apply(100);
        var result = shot.Roll(impale, new Rolls(0, 0, 0), mitigate: (_, _) => 25,
            criticalMultiplier: 150, reflect: stored => (int)Math.Round(stored * .5));
        Assert.Equal((43, true), result); // direct round(25 * 1.5), reflected 10 * .5
        Assert.Equal(30, impale.Bonus); // old 10 plus 10% of raw 100 * owner's 2.0 critical multiplier
    }

    [Fact]
    public void Impale_records_the_actual_noncritical_roll_instead_of_the_weapons_maximum()
    {
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 10, 20), 100, 0, 150, 100);
        var impale = new ImpaleState();
        Assert.Equal((10, false), shot.Roll(impale, new Rolls(0, 0, 0)));
        Assert.Equal(1, impale.Bonus);
    }

    [Fact]
    public void A_new_critical_hit_does_not_multiply_already_stored_reflected_damage()
    {
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 10, 20), 100, 10000, 200, 100);
        var impale = new ImpaleState();
        impale.Apply(100);
        Assert.Equal((30, true), shot.Roll(impale, new Rolls(0, 0, 0))); // direct 10 * 2, reflected 10
        Assert.Equal(12, impale.Bonus); // old 10 plus the new critical physical hit's 2
    }

    [Fact]
    public void Capture_freezes_local_penetration_and_new_shots_follow_weapon_removal()
    {
        var local = new Dictionary<string, int> { ["local_elemental_penetration_%"] = 9 };
        var weapon = new SkillCatalog.Unarmed(1000, 10, 10, 120) { LocalSources = local };
        var shot = OrdinaryRangedShot.Capture(weapon, new Dictionary<uint, int>(), [], Program.Stats, 100, 0, 150);
        local.Clear();
        var next = OrdinaryRangedShot.Capture(weapon, new Dictionary<uint, int>(), [], Program.Stats, 100, 0, 150);
        Assert.Equal(new(9, 9, 9, 0), shot.Penetration);
        Assert.Equal(default, next.Penetration);
    }

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
            // Ranged weapon damage excludes melee-only increases, and the player's conversion wins
            // a colliding default-skill key. Added fire is added before conversion, exactly once.
            Assert.Equal(new DamageRange(2.5, 6.5), shot.Hit[DamageType.Physical]);
            Assert.Equal(new DamageRange(5.5, 13.5), shot.Hit[DamageType.Fire]);
            raw.Clear();
            weapon = weapon with { MinDamage = 1000, MaxDamage = 2000 };
            Assert.Equal(new DamageRange(2.5, 6.5), shot.Hit[DamageType.Physical]);
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

    [Fact]
    public void Failed_critical_confirmation_selects_noncritical_mitigation_and_impale_storage()
    {
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 10, 10), 100, 10000, 200, 100);
        var impale = new ImpaleState();
        impale.Apply(100);
        var result = shot.Roll(impale, new Rolls(0, 0), mitigate: (rolled, critical) =>
        {
            Assert.False(critical);
            return (int)rolled.MinTotal;
        }, criticalRandom: new Rolls(0), hitChance: 5, accuracyRandom: new Rolls(99));
        Assert.Equal((20, false), result); // Normal direct 10 plus the existing reflected 10.
        Assert.Equal(2, impale.Stacks);
        Assert.Equal(11, impale.Bonus); // New physical 10 stores 1, not the unconfirmed critical's 2.
    }

    [Fact]
    public void Existing_impales_are_reflected_separately_and_a_new_stack_keeps_five_hits()
    {
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 10, 20), 100, 10_000, 200, 100);
        var impale = new ImpaleState();
        impale.Apply(100);
        var result = shot.Roll(impale, new Rolls(0, 0, 0));
        Assert.Equal((30, true), result); // direct 10 * 200%, plus 10 reflected damage
        Assert.Equal(12, impale.Bonus); // Existing 10 plus 10% of this hit's critical physical roll (10 * 2).
        for (int i = 0; i < 4; i++) impale.AfterHit();
        Assert.Equal(2, impale.Bonus); // The older stack expires first; the newly applied one retains its final hit.
    }

}

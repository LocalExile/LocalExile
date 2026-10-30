using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    static void EquipOffHand(Contact f, string name) =>
        PlayerInventories.For(f.Conn.Character!, Program.Items).Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find(name)!));

    [Fact]
    public void Dual_wielding_publishes_the_off_hand_rows_from_that_weapon()
    {
        using var f = new Contact(new Draws(), null, "Rusted Sword");
        f.Conn.PlayerLife = 51;
        f.Set(AttackBaseCriticalStrike.WeaponOverrideStat, 0);
        f.Refresh();
        var stats = Program.CharacterPlayerStats(f.Conn);
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(0, Get("skill_uses_off_hand"));
        Assert.Equal(1, Get("skill_uses_main_hand"));
        Assert.Equal(0, Get("off_hand_critical_strike_chance"));

        EquipOffHand(f, "Glass Shank");
        f.Refresh();
        stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(1, Get("skill_uses_off_hand"));
        var dagger = Program.Items.Find("Glass Shank")!;
        Assert.NotEqual(Program.Items.WeaponCritical(Program.Items.Find("Rusted Sword")!), Program.Items.WeaponCritical(dagger));
        int Raw(string name) => Get(name);
        Assert.Equal(AttackBaseCriticalStrike.Chance(true, Program.Items.WeaponCritical(dagger), f.Conn.Pools.CriticalStrikeChancePct, Raw),
            Get("off_hand_critical_strike_chance"));
        Assert.Equal(Get("main_hand_critical_strike_chance") * Program.Items.WeaponCritical(dagger),
            Get("off_hand_critical_strike_chance") * Program.Items.WeaponCritical(Program.Items.Find("Rusted Sword")!));
        Assert.Equal(Get("main_hand_critical_strike_multiplier"), Get("off_hand_critical_strike_multiplier"));
        Assert.Equal(Get("main_hand_accuracy_rating"), Get("off_hand_accuracy_rating"));
        Assert.Equal(Get("main_hand_chance_to_hit_%"), Get("off_hand_chance_to_hit_%"));
        Assert.Equal(Get("main_hand_chance_to_hit_evasive_monsters_%"), Get("off_hand_chance_to_hit_evasive_monsters_%"));
        Assert.Equal(Get("main_hand_attack_speed_+%"), Get("off_hand_attack_speed_+%"));

        // The sent stats carry the off hand's own damage rows (WeaponStats): a dagger swings its own physical range.
        var sent = ((IEnumerable<(uint, int)>)Method("FinalPlayerStats").Invoke(null, [f.Conn])!)
            .ToDictionary(s => s.Item1, s => s.Item2);
        int Sent(string name) => sent.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        var off = (SkillCatalog.Unarmed)Method("OffhandWeaponFor").Invoke(null, [f.Conn])!;
        var offHit = Program.AttackHitConverted(off, stats, true, null);
        Assert.Equal((int)Math.Floor(offHit.MaxTotal), Sent("off_hand_maximum_total_damage"));
        Assert.Equal((int)Math.Floor(offHit[DamageType.Chaos].Min), Sent("off_hand_minimum_chaos_damage"));
    }

    [Fact]
    public void A_dual_wielded_attack_gem_publishes_off_hand_rows_with_its_supports()
    {
        using var f = new Contact(new Draws(), "Cyclone", "Rusted Sword");
        f.Conn.PlayerLife = 51;
        var before = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(0, before.GetValueOrDefault(Program.Stats.Key("skill_uses_off_hand")!.Value));
        EquipOffHand(f, "Rusted Sword");
        f.Refresh();
        var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        int Get(string name) => panel.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(1, Get("skill_uses_off_hand"));
        // Identical swords: the off hand mirrors every main-hand row, supports included.
        foreach (string row in new[] { "critical_strike_chance", "critical_strike_multiplier", "accuracy_rating", "chance_to_hit_%",
            "chance_to_hit_evasive_monsters_%", "attack_speed_+%", "minimum_total_damage", "maximum_total_damage",
            "minimum_physical_damage", "maximum_chaos_damage" })
            Assert.Equal(Get("main_hand_" + row), Get("off_hand_" + row));
        Assert.True(Get("off_hand_maximum_total_damage") > 0);
        Assert.True(Get("off_hand_critical_strike_multiplier") > 150, "the linked crit damage support raises both hands");
    }
}

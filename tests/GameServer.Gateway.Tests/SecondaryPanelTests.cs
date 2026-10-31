using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Fact]
    public void Bear_Trap_publishes_secondary_rows_from_its_own_base_damage()
    {
        using var f = new Contact(new Draws(), "BearTrap", "Rusted Sword");
        f.Conn.PlayerLife = 51;
        f.Set("physical_damage_+%", 100);
        f.Set("damage_+%", 50);
        f.Refresh();
        int effect = Program.Skills.EffectRow("BearTrap");
        // Bear Trap carries the Spell type but has no spell base damage: its hit is the secondary one.
        Assert.True(Program.Skills.HitDamage(effect, 1).IsZero);
        var gem = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill);
        var panel = gem.Stats!.ToDictionary(s => s.Key, s => s.Value);
        int Get(string name) => panel.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        var baseHit = Program.Skills.SecondaryHitDamage(effect, 1);
        Assert.True(baseHit[DamageType.Physical].Max > 0);
        Assert.Equal(1, Get("display_skill_deals_secondary_damage"));
        Assert.Equal(1, Get("skill_show_average_damage_instead_of_dps"));
        // Strength's melee bonus and the attack/spell buckets do not apply: generic and physical only (×2.5).
        Assert.Equal((int)Math.Floor(baseHit[DamageType.Physical].Min * 2.5), Get("secondary_minimum_physical_damage"));
        Assert.Equal((int)Math.Floor(baseHit[DamageType.Physical].Max * 2.5), Get("secondary_maximum_total_damage"));
        Assert.Equal(0, Get("secondary_maximum_chaos_damage"));
        Assert.True(Get("secondary_critical_strike_chance") >= 0);
        // Base 150 plus the fixture's linked Increased Critical Damage support (+100).
        Assert.Equal(250, Get("secondary_critical_strike_multiplier"));
        Assert.Equal(0, Get("hundred_times_damage_per_second"));
        Assert.True(Get("display_hundred_times_damage_per_skill_use") > 0);
        // The secondary hit's poison from its physical damage: 30% per second, per minute.
        double average = (Math.Floor(baseHit[DamageType.Physical].Min * 2.5) + Math.Floor(baseHit[DamageType.Physical].Max * 2.5)) / 2;
        Assert.True(Get("minimum_poison_damage_to_deal_per_minute_from_non_critical_secondary_damage") > 0);
        Assert.Equal((int)Math.Round(baseHit[DamageType.Physical].Max * 2.5 * .3 * 60),
            Get("maximum_poison_damage_to_deal_per_minute_from_non_critical_secondary_damage"));
        Assert.Equal(0, Get("maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        _ = average;
    }
}

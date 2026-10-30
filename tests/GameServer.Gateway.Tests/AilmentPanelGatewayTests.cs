using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Fact]
    public void Default_attack_publishes_per_hand_ailment_rows_from_its_raw_hit()
    {
        using var f = new Contact(new Draws(), null, "Rusted Sword");
        f.Conn.PlayerLife = 51;
        // The fixture adds 1000 flat chaos and -100% physical; take the physical bucket below strength's melee bonus
        // too, so poison comes from the chaos portion only.
        f.Set("physical_damage_+%", -100_000);
        var stats = Program.CharacterPlayerStats(f.Conn);
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(0, Get("maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(0, Get("maximum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(1000 * 30 / 100 * 60, Get("minimum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(1000 * 30 / 100 * 60, Get("maximum_poison_damage_to_deal_per_minute_from_critical_main_hand_damage"));
        Assert.Equal(0, Get("maximum_poison_damage_to_deal_per_minute_from_non_critical_off_hand_damage"));
        f.Set("critical_ailment_dot_multiplier_+", 100);
        f.Set("base_poison_damage_+%", 50);
        stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(1000 * 30 / 100 * 60 * 3 / 2, Get("minimum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(1000 * 30 / 100 * 60 * 3, Get("minimum_poison_damage_to_deal_per_minute_from_critical_main_hand_damage"));
        EquipOffHand(f, "Rusted Sword");
        f.Refresh();
        stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(Get("minimum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"),
            Get("minimum_poison_damage_to_deal_per_minute_from_non_critical_off_hand_damage"));
        Assert.True(Get("minimum_poison_damage_to_deal_per_minute_from_non_critical_off_hand_damage") > 0);
    }

    [Theory]
    [InlineData("Fireball", "spell")]
    [InlineData("Cyclone", "main_hand")]
    public void Selected_skills_publish_their_ailment_rows_in_their_context(string gem, string hand)
    {
        using var f = new Contact(new Draws(), gem, "Rusted Sword");
        f.Conn.PlayerLife = 51;
        var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        int Get(string name) => panel.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        var raw = (DamagePacket)Method("SupportedHit").Invoke(null, [f.Conn, f.Skill])!;
        Assert.True(raw[DamageType.Chaos].Min > 0);
        int poison = Get($"minimum_poison_damage_to_deal_per_minute_from_non_critical_{hand}_damage");
        Assert.True(poison > 0);
        Assert.Equal(poison, Get($"minimum_poison_damage_to_deal_per_minute_from_critical_{hand}_damage"));
        Assert.Equal(0, Get($"minimum_poison_damage_to_deal_per_minute_from_non_critical_{(hand == "spell" ? "main_hand" : "spell")}_damage"));
        if (gem == "Fireball")
        {
            Assert.True(Get("minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage") > 0);
            Assert.Equal(0, Get("display_skill_deals_secondary_damage"));
            Assert.Equal(0, Get("secondary_maximum_total_damage"));
        }
    }
}

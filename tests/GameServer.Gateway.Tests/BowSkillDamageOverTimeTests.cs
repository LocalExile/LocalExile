using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class BowSkillDamageOverTimeTests
{
    [Fact]
    public void Heavy_draw_increases_bow_bleeding_and_leaves_the_hit_alone()
    {
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100);
        const string modifier = "damage_over_time_+%_with_bow_skills";
        int Get(string name, int weapon) => name switch
        {
            modifier => 30,
            "main_hand_weapon_type" => weapon,
            _ => 0
        };
        int Bleed(int weapon) => AilmentPanelStats.Compute(new("main_hand", Spell: false), raw, new(),
            name => Get(name, weapon)).Single(row =>
                row.Stat == "maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage").Value;

        Assert.Equal(5460, Bleed(WeaponTypeStats.Bow));
        Assert.Equal(4200, Bleed(WeaponTypeStats.Of("One Hand Sword")));
        var hit = PlayerHitDamage.Resolve(raw, name => Get(name, WeaponTypeStats.Bow), new(), spell: false,
            wieldingBow: true);
        Assert.Equal(100, hit[DamageType.Physical].Max);
    }
}

using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Global added damage ("Adds # to # Fire Damage" on a ring) raises attacks and spells alike.</summary>
public class GlobalAddedDamageTests
{
    [Fact]
    public void Global_added_damage_reaches_attacks_and_spells()
    {
        var stats = new Dictionary<string, int>
        {
            ["global_minimum_added_fire_damage"] = 10,
            ["global_maximum_added_fire_damage"] = 20,
            ["attack_minimum_added_fire_damage"] = 1,
            ["attack_maximum_added_fire_damage"] = 2,
            ["spell_minimum_added_fire_damage"] = 3,
            ["spell_maximum_added_fire_damage"] = 4,
        };
        int Get(string name) => stats.GetValueOrDefault(name);

        var attack = DamageStats.RawAdded(DamagePacket.Empty, Get, spell: false);
        Assert.Equal(11, attack[DamageType.Fire].Min);
        Assert.Equal(22, attack[DamageType.Fire].Max);
        var spell = DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true);
        Assert.Equal(13, spell[DamageType.Fire].Min);
        Assert.Equal(24, spell[DamageType.Fire].Max);
    }
}

using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Global added damage ("Adds # to # Fire Damage" on a ring) raises attacks and spells alike.</summary>
public class GlobalAddedDamageTests
{
    [Fact]
    public void Global_added_damage_reaches_attacks_and_spells()
    {
        var stats = new Dictionary<uint, int>
        {
            [Stat.GlobalMinimumAddedFireDamage] = 10,
            [Stat.GlobalMaximumAddedFireDamage] = 20,
            [Stat.AttackMinimumAddedFireDamage] = 1,
            [Stat.AttackMaximumAddedFireDamage] = 2,
            [Stat.SpellMinimumAddedFireDamage] = 3,
            [Stat.SpellMaximumAddedFireDamage] = 4,
        };
        int Get(uint name) => stats.GetValueOrDefault(name);

        var attack = DamageStats.RawAdded(DamagePacket.Empty, Get, spell: false);
        Assert.Equal(11, attack[DamageType.Fire].Min);
        Assert.Equal(22, attack[DamageType.Fire].Max);
        var spell = DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true);
        Assert.Equal(13, spell[DamageType.Fire].Min);
        Assert.Equal(24, spell[DamageType.Fire].Max);
    }
}

using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CharmPhysicalAddedTests
{
    [Fact]
    public void Global_physical_added_if_crit_recently_counts_for_attacks_and_spells()
    {
        static int Get(string name) => name switch
        {
            "minimum_added_physical_damage_if_have_crit_recently" => 12,
            "maximum_added_physical_damage_if_have_crit_recently" => 22,
            _ => 0,
        };
        foreach (bool spell in new[] { false, true })
        {
            var hit = DamageStats.RawAdded(DamagePacket.Empty, Get, spell);
            Assert.Equal(12, hit[DamageType.Physical].Min);
            Assert.Equal(22, hit[DamageType.Physical].Max);
        }
    }
}
